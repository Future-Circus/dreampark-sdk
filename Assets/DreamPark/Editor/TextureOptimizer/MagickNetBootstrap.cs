#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// Reflection-based wrapper around the Magick.NET assembly. We never
    /// take a compile-time dependency on Magick.NET — the DLLs are
    /// downloaded per-machine by <see cref="MagickNetInstaller"/> on
    /// first use of the Texture Optimizer, so the SDK has to compile
    /// without them.
    ///
    /// At runtime (after install), we sniff loaded assemblies for
    /// `ImageMagick.MagickImage`, cache the MethodInfo/PropertyInfo
    /// handles once, then call them per texture. Reflection overhead is
    /// negligible compared to disk I/O + image encoding.
    ///
    /// Why Magick.NET at all when Unity's built-in encoders work? Two
    /// things: Lanczos resize quality (visibly cleaner 4K→1K downscales
    /// than Unity's bilinear blit) and direct file-byte access (no need
    /// to mark every source texture readable+uncompressed and trigger
    /// two AssetDatabase re-imports per re-encode). On a 500-texture
    /// batch that's 5-10× faster wall-clock.
    /// </summary>
    public static class MagickNetBootstrap
    {
        // Cached reflection handles. `_resolvedSuccessfully` flips to true
        // only when EnsureResolved finds a working binding — failed
        // lookups don't latch, so subsequent calls after install (or
        // after a domain reload that loads the new assembly) re-scan
        // and pick up the new types.
        private static bool _resolvedSuccessfully;
        private static Type _magickImageType;          // ImageMagick.MagickImage
        private static Type _magickFormatType;         // ImageMagick.MagickFormat
        private static MethodInfo _readMethod;         // MagickImage.Read(string)
        private static MethodInfo _resizeMethod;       // MagickImage.Resize(int, int)
        private static MethodInfo _writeMethod;        // MagickImage.Write(string)
        private static PropertyInfo _formatProp;       // MagickImage.Format
        private static PropertyInfo _qualityProp;      // MagickImage.Quality
        private static PropertyInfo _widthProp;
        private static PropertyInfo _heightProp;

        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _magickImageType != null
                    && _readMethod != null
                    && _writeMethod != null
                    && _formatProp != null;
            }
        }

        public static string StatusMessage
        {
            get
            {
                EnsureResolved();
                if (_magickImageType == null)
                    return "Magick.NET not loaded — first use will download it.";
                return "Magick.NET v" + GetVersion() + " ready.";
            }
        }

        // ─── Encode entry point — called by the Executor ────────────────

        /// <summary>
        /// Read <paramref name="sourcePath"/>, resize so the largest
        /// dimension equals <paramref name="maxLargestDim"/> (preserving
        /// aspect ratio), and write the result to
        /// <paramref name="destinationPath"/> in the requested
        /// <paramref name="format"/>. For JPG, <paramref name="jpgQuality"/>
        /// is the encoder quality (1-100).
        /// </summary>
        public static void ReEncode(
            string sourcePath,
            string destinationPath,
            TargetFormat format,
            int maxLargestDim,
            int jpgQuality)
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    "Magick.NET is not installed. " + StatusMessage);

            object img = Activator.CreateInstance(_magickImageType);
            try
            {
                _readMethod.Invoke(img, new object[] { sourcePath });

                int w = Convert.ToInt32(_widthProp.GetValue(img));
                int h = Convert.ToInt32(_heightProp.GetValue(img));

                int srcLargest = Mathf.Max(w, h);
                if (srcLargest > maxLargestDim && srcLargest > 0)
                {
                    // The Resize overload scales to fit inside a
                    // (width, height) box. Magick.NET 14.x uses uint;
                    // older versions used int. EnsureResolved picked
                    // whichever it found — we match the param type
                    // here so Invoke doesn't trip on a type mismatch.
                    var (tw, th) = TextureOptimizationPlanner.ScaleKeepingAspect(w, h, maxLargestDim);
                    var resizeParams = _resizeMethod.GetParameters();
                    object[] args = resizeParams.Length > 0 && resizeParams[0].ParameterType == typeof(uint)
                        ? new object[] { (uint)tw, (uint)th }
                        : new object[] { tw, th };
                    _resizeMethod.Invoke(img, args);
                }

                object magickFormat = Enum.Parse(_magickFormatType, format == TargetFormat.JPG ? "Jpeg" : "Png");
                _formatProp.SetValue(img, magickFormat);

                if (format == TargetFormat.JPG && _qualityProp != null)
                {
                    int q = Mathf.Clamp(jpgQuality, 1, 100);
                    // 14.x: uint. Older: int. Match the property type.
                    object qVal = _qualityProp.PropertyType == typeof(uint) ? (object)(uint)q : (object)q;
                    _qualityProp.SetValue(img, qVal);
                }

                var destFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destFolder) && !Directory.Exists(destFolder))
                    Directory.CreateDirectory(destFolder);

                _writeMethod.Invoke(img, new object[] { destinationPath });
            }
            finally
            {
                if (img is IDisposable disposable) disposable.Dispose();
            }
        }

        /// <summary>
        /// Sample the actual alpha channel and report whether any pixel
        /// has alpha &lt; 250 (our "really uses transparency" threshold).
        /// Catches "vendor exported with alpha channel but it's all 255"
        /// so the executor can downgrade PNG → JPG.
        ///
        /// Uses Magick.NET's Statistics() histogram which is accurate and
        /// fast (~20-50 ms even on 4K textures). Falls back to "yes" on
        /// any error so we err on preserving alpha.
        /// </summary>
        public static bool HasMeaningfulAlpha(string sourcePath)
        {
            if (!IsAvailable) return true;

            object img = Activator.CreateInstance(_magickImageType);
            try
            {
                _readMethod.Invoke(img, new object[] { sourcePath });

                var statsMethod = _magickImageType.GetMethod("Statistics", Type.EmptyTypes);
                if (statsMethod == null) return true;

                var stats = statsMethod.Invoke(img, null);
                if (stats == null) return true;

                var getChannel = stats.GetType().GetMethod("GetChannel");
                if (getChannel == null) return true;

                // PixelChannel lives in Magick.NET.Core in 14.x (not in
                // the Q8 assembly that exports MagickImage), so search
                // every loaded Magick.NET* assembly.
                var pixelChannel = FindTypeInMagickAssemblies("ImageMagick.PixelChannel");
                if (pixelChannel == null) return true;

                object alphaEnum = Enum.Parse(pixelChannel, "Alpha");
                var channelStats = getChannel.Invoke(stats, new[] { alphaEnum });
                if (channelStats == null) return true;

                var minProp = channelStats.GetType().GetProperty("Minimum");
                if (minProp == null) return true;

                double min = Convert.ToDouble(minProp.GetValue(channelStats));
                // Q8: max 255. Q16: 65535. We pinned Q8-AnyCPU, so 250/255.
                int maxValue = GetDepthBits() == 16 ? 65535 : 255;
                return min < maxValue * (250.0 / 255.0);
            }
            catch
            {
                return true;
            }
            finally
            {
                if (img is IDisposable disposable) disposable.Dispose();
            }
        }

        // ─── Internal helpers ───────────────────────────────────────────

        private static int GetDepthBits()
        {
            if (_magickImageType == null) return 8;
            string asmName = _magickImageType.Assembly.GetName().Name ?? "";
            if (asmName.Contains("Q16")) return 16;
            return 8;
        }

        private static string GetVersion()
        {
            if (_magickImageType == null) return "?";
            return _magickImageType.Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Look for the Magick.NET assembly in the current AppDomain.
        /// Only successful resolves latch — a failed scan never sets
        /// `_resolvedSuccessfully`, so post-install / post-domain-reload
        /// calls re-scan and pick up the freshly loaded assembly.
        /// </summary>
        private static void EnsureResolved()
        {
            if (_resolvedSuccessfully && _magickImageType != null) return;
            _magickImageType = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;
                // The Q8-AnyCPU package's managed DLL exports a type
                // called ImageMagick.MagickImage in an assembly whose
                // name starts with "Magick.NET". Magick.NET.Core
                // doesn't export MagickImage, only the abstract base —
                // skipping it would be premature, the GetType check
                // below filters correctly.
                if (!name.StartsWith("Magick.NET", StringComparison.OrdinalIgnoreCase)) continue;

                var magickImage = asm.GetType("ImageMagick.MagickImage");
                if (magickImage == null) continue;

                _magickImageType = magickImage;
                // MagickFormat lives in Magick.NET.Core in 14.x, not in
                // the Q8-AnyCPU assembly where MagickImage is defined.
                // Probe every loaded Magick.NET* assembly so we find it
                // regardless of which one happens to be searched first.
                _magickFormatType = FindTypeInMagickAssemblies("ImageMagick.MagickFormat");
                _readMethod = magickImage.GetMethod("Read", new[] { typeof(string) });

                // Magick.NET 14.x signature: Resize(uint, uint). Earlier
                // versions exposed (int, int). Try the new signature
                // first, then the old one — keeps the bootstrap working
                // across a Magick.NET pin bump in either direction.
                _resizeMethod = magickImage.GetMethod("Resize", new[] { typeof(uint), typeof(uint) })
                             ?? magickImage.GetMethod("Resize", new[] { typeof(int), typeof(int) });
                _writeMethod = magickImage.GetMethod("Write", new[] { typeof(string) });
                _formatProp = magickImage.GetProperty("Format");
                _qualityProp = magickImage.GetProperty("Quality");
                _widthProp = magickImage.GetProperty("Width");
                _heightProp = magickImage.GetProperty("Height");

                // Only latch on a fully successful resolve. If a future
                // Magick.NET version removes one of these members,
                // _resolvedSuccessfully stays false and IsAvailable
                // reports false until the user updates the bootstrap.
                _resolvedSuccessfully = _readMethod != null
                                     && _writeMethod != null
                                     && _formatProp != null;
                break;
            }
        }

        /// <summary>
        /// Force a re-scan of loaded assemblies. Called by the window
        /// just after MagickNetInstaller.InstallSync completes but
        /// before the domain reload actually fires (the new DLLs are on
        /// disk; once AssetDatabase.Refresh triggers a recompile, the
        /// reopen hook re-evaluates IsAvailable in the fresh domain).
        /// </summary>
        public static void Invalidate()
        {
            _resolvedSuccessfully = false;
            _magickImageType = null;
        }

        /// <summary>
        /// Probe every loaded assembly whose name starts with
        /// "Magick.NET" for a type. Needed because Magick.NET 14.x
        /// splits the API across multiple DLLs: <c>MagickImage</c> is
        /// in <c>Magick.NET-Q8-AnyCPU</c>, but enums like
        /// <c>MagickFormat</c> and <c>PixelChannel</c> live in
        /// <c>Magick.NET.Core</c>. Looking only at the assembly that
        /// hosts <see cref="_magickImageType"/> misses them.
        /// </summary>
        private static Type FindTypeInMagickAssemblies(string fullTypeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.StartsWith("Magick.NET", StringComparison.OrdinalIgnoreCase)) continue;
                var t = asm.GetType(fullTypeName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
#endif
