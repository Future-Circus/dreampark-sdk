#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// Fallback encoder used when Magick.NET refuses to read a source
    /// file. Magick.NET's TGA/TIF readers are strict about format
    /// quirks (origin-flag variants, BigTIFF, 16-bit-per-channel,
    /// multi-page) and a small fraction of vendor textures trip them.
    /// Unity's own TextureImporter is more permissive — it'll already
    /// have imported those weird files at editor startup, so we can
    /// load the imported <see cref="Texture2D"/> via AssetDatabase
    /// and encode through Unity's built-in PNG/JPG writers.
    ///
    /// Cost vs Magick.NET:
    ///   - One extra re-import to flip Read/Write + Uncompressed on
    ///     the source (a few hundred ms per file).
    ///   - Bilinear blit instead of Lanczos for downscale. Visibly
    ///     softer at aggressive ratios; fine for the once-per-year
    ///     files that hit this path.
    /// </summary>
    public static class UnityImageProcessor
    {
        /// <summary>
        /// Same signature as <see cref="MagickNetBootstrap.ReEncode"/> so
        /// the executor can swap them. Reads via AssetDatabase, resizes
        /// via Graphics.Blit, writes via EncodeToPNG/JPG. Restores the
        /// source's TextureImporter Read/Write + Compression on exit.
        /// </summary>
        public static void ReEncode(
            string srcAssetPath,
            string dstAbsPath,
            TargetFormat format,
            int maxLargestDim,
            int jpgQuality)
        {
            if (format == TargetFormat.KeepAsIs)
                throw new ArgumentException("ReEncode called with KeepAsIs.");

            var importer = AssetImporter.GetAtPath(srcAssetPath) as TextureImporter;
            if (importer == null)
                throw new InvalidOperationException(
                    "No TextureImporter for " + srcAssetPath +
                    " — Unity hasn't imported this file. The Magick.NET path " +
                    "would normally handle this; the fallback can't.");

            bool wasReadable = importer.isReadable;
            var wasCompression = importer.textureCompression;
            bool needsRetoggle = !wasReadable || wasCompression != TextureImporterCompression.Uncompressed;

            if (needsRetoggle)
            {
                importer.isReadable = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            try
            {
                var src = AssetDatabase.LoadAssetAtPath<Texture2D>(srcAssetPath);
                if (src == null)
                    throw new InvalidOperationException(
                        "Unity returned no Texture2D for " + srcAssetPath +
                        " — the file may be a non-standard format Unity didn't import either.");

                int srcW = src.width;
                int srcH = src.height;
                int srcLargest = Mathf.Max(srcW, srcH);
                Texture2D toEncode = src;
                bool createdNew = false;

                if (srcLargest > maxLargestDim && srcLargest > 0)
                {
                    var (tw, th) = TextureOptimizationPlanner.ScaleKeepingAspect(srcW, srcH, maxLargestDim);
                    toEncode = DownscaleViaBlit(src, tw, th, format);
                    createdNew = true;
                }
                else
                {
                    // No resize needed, but Texture2D.EncodeToPNG/JPG
                    // requires a readable, RGBA32-ish texture. Blit
                    // through a temporary RT to guarantee that.
                    toEncode = DownscaleViaBlit(src, srcW, srcH, format);
                    createdNew = true;
                }

                byte[] bytes = format == TargetFormat.JPG
                    ? toEncode.EncodeToJPG(jpgQuality)
                    : toEncode.EncodeToPNG();

                var destFolder = Path.GetDirectoryName(dstAbsPath);
                if (!string.IsNullOrEmpty(destFolder) && !Directory.Exists(destFolder))
                    Directory.CreateDirectory(destFolder);

                File.WriteAllBytes(dstAbsPath, bytes);

                if (createdNew) UnityEngine.Object.DestroyImmediate(toEncode);
            }
            finally
            {
                if (needsRetoggle)
                {
                    importer.isReadable = wasReadable;
                    importer.textureCompression = wasCompression;
                    importer.SaveAndReimport();
                }
            }
        }

        private static Texture2D DownscaleViaBlit(Texture2D src, int tw, int th, TargetFormat format)
        {
            var readWrite = format == TargetFormat.JPG
                ? RenderTextureReadWrite.sRGB
                : RenderTextureReadWrite.Default;

            var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, readWrite);
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var dst = new Texture2D(tw, th, TextureFormat.RGBA32, mipChain: false, linear: format != TargetFormat.JPG);
                dst.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                dst.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return dst;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
#endif
