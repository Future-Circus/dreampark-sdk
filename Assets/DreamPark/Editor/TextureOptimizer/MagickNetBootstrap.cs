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
        private static PropertyInfo _hasAlphaProp;     // MagickImage.HasAlpha (bool get/set)
        private static MethodInfo _getAttributeMethod; // MagickImage.GetAttribute(string)
        private static MethodInfo _negateChannelMethod;// MagickImage.Negate(Channels)
        private static Type _channelsType;             // ImageMagick.Channels (enum)

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

                // ── TIFF "ExtraSamples = unspecified" alpha repair ──────
                // When a TIFF's 4th channel exists but its ExtraSamples tag
                // is SAMPLEFORMAT_UNSPECIFIED (e.g. some DCC tools export
                // this way), ImageMagick interprets the channel as MATTE
                // (high = transparent). PNG semantics use ALPHA (high =
                // opaque). So a source with alpha mean 35 (decal: mostly
                // transparent) gets written as alpha mean 220 (mostly
                // opaque) — the channel is read fine, then inverted by the
                // format conversion.
                //
                // Fix: if the source is a TIFF AND its ExtraSamples tag is
                // 0 (unspecified), negate the alpha channel after Read so
                // the internal representation lines up with PNG opacity
                // semantics. We parse the TIFF header directly rather than
                // relying on Magick.NET's GetAttribute("tiff:alpha") —
                // that one doesn't always return a value, depending on
                // reader policy and Magick.NET version.
                //
                // TIFFs with associated/unassociated alpha (the well-defined
                // cases) skip this branch and stay untouched.
                if (_negateChannelMethod != null && _channelsType != null
                    && IsTiffExtraSamplesUnspecified(sourcePath))
                {
                    try
                    {
                        object alphaChannel = Enum.Parse(_channelsType, "Alpha");
                        _negateChannelMethod.Invoke(img, new object[] { alphaChannel });
                        Debug.Log("[TextureOptimizer] Negated alpha for TIFF with " +
                                  "ExtraSamples=unspecified: '" + sourcePath + "'.");
                    }
                    catch (Exception negateErr)
                    {
                        Debug.LogWarning(
                            "[TextureOptimizer] Could not negate alpha for "
                            + "tiff:alpha=unspecified file '" + sourcePath + "': "
                            + negateErr.Message + ". Output PNG may have inverted alpha.");
                    }
                }

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

                // Force HasAlpha = true for PNG output. Fixes TIFFs with
                // `tiff:alpha: unspecified` (the ExtraSamples tag set to
                // SAMPLEFORMAT_UNSPECIFIED): ImageMagick reads the 4th
                // channel's pixel data but doesn't auto-promote it to a
                // live alpha channel, so PNG export drops it. Declaring
                // alpha active before write tells the encoder to keep
                // the channel. Idempotent for already-alpha sources, and
                // skipped for JPG (which doesn't have alpha anyway).
                if (format == TargetFormat.PNG && _hasAlphaProp != null)
                {
                    try { _hasAlphaProp.SetValue(img, true); }
                    catch { /* older Magick.NET where HasAlpha is read-only;
                               fall through and accept whatever the encoder does */ }
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

        // ── TIFF header parser ──────────────────────────────────────────
        // Returns true if the file at `path` is a TIFF whose ExtraSamples
        // tag (338) is present and contains a 0 (SAMPLEFORMAT_UNSPECIFIED).
        //
        // PUBLIC because the TextureOptimizationExecutor uses it to route
        // these files away from Magick.NET (which interprets the 4th
        // channel as matte-transparency and inverts during PNG write) and
        // directly through Unity's own encoder (which handles the file
        // correctly via its own TIFF reader — confirmed by "alpha works
        // in-game" testimony from users hitting this with the original
        // imported TIFs).
        //
        // We parse the TIFF directly because Magick.NET's
        // GetAttribute("tiff:alpha") doesn't always populate after Read —
        // depends on reader policy and Magick.NET version. Header parse
        // is ~30 lines, always-correct, no reflection.
        //
        // TIFF layout: 8-byte header → IFD with N entries (12 bytes each).
        // Each entry: tag(2) + type(2) + count(4) + valueOrOffset(4).
        // ExtraSamples (tag 338) is type SHORT (3), so the value fits in
        // the entry's value field when count == 1. With multiple samples
        // (count > 1), the value field is an offset to where the array
        // lives — but the first value's location is computable.
        public static bool IsTiffExtraSamplesUnspecified(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path);
            if (!string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new System.IO.BinaryReader(fs))
                {
                    // 8-byte header.
                    byte b1 = br.ReadByte();
                    byte b2 = br.ReadByte();
                    bool littleEndian;
                    if (b1 == 'I' && b2 == 'I') littleEndian = true;
                    else if (b1 == 'M' && b2 == 'M') littleEndian = false;
                    else return false;

                    ushort magic = ReadUInt16(br, littleEndian);
                    if (magic != 42) return false;

                    uint ifdOffset = ReadUInt32(br, littleEndian);
                    fs.Seek(ifdOffset, SeekOrigin.Begin);

                    ushort entryCount = ReadUInt16(br, littleEndian);
                    for (int i = 0; i < entryCount; i++)
                    {
                        ushort tag      = ReadUInt16(br, littleEndian);
                        ushort type     = ReadUInt16(br, littleEndian);
                        uint count      = ReadUInt32(br, littleEndian);
                        uint valueField = ReadUInt32(br, littleEndian);

                        if (tag != 338) continue;  // ExtraSamples

                        // Type SHORT (3): 2 bytes per value. If count == 1,
                        // the value sits in valueField's low 2 bytes (in
                        // the endian of the file).
                        if (count >= 1 && type == 3)
                        {
                            ushort firstSample;
                            if (count == 1)
                            {
                                // valueField low 2 bytes are the value
                                // (endian already applied by ReadUInt32).
                                firstSample = (ushort)(valueField & 0xFFFF);
                            }
                            else
                            {
                                // Multiple values → valueField is an offset.
                                long savedPos = fs.Position;
                                fs.Seek(valueField, SeekOrigin.Begin);
                                firstSample = ReadUInt16(br, littleEndian);
                                fs.Position = savedPos;
                            }
                            return firstSample == 0;  // 0 = unspecified
                        }
                    }
                }
            }
            catch
            {
                // Malformed TIFF, file access denied, etc. — skip the fix,
                // let the encoder do whatever it does.
                return false;
            }

            return false;
        }

        private static ushort ReadUInt16(System.IO.BinaryReader br, bool littleEndian)
        {
            byte a = br.ReadByte();
            byte b = br.ReadByte();
            return littleEndian ? (ushort)(a | (b << 8)) : (ushort)((a << 8) | b);
        }

        private static uint ReadUInt32(System.IO.BinaryReader br, bool littleEndian)
        {
            byte a = br.ReadByte();
            byte b = br.ReadByte();
            byte c = br.ReadByte();
            byte d = br.ReadByte();
            return littleEndian
                ? (uint)(a | (b << 8) | (c << 16) | (d << 24))
                : (uint)((a << 24) | (b << 16) | (c << 8) | d);
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
                // HasAlpha is bool get/set. We force it true before PNG
                // export to handle TIFFs whose ExtraSamples tag is set to
                // "unspecified" — ImageMagick won't auto-promote that to
                // a live alpha channel, but the pixel data is present,
                // so we just declare it as alpha and PNG preserves it.
                // Optional capability: if a future Magick.NET version
                // renames the property, this stays null and the encode
                // path falls back to the existing behavior gracefully.
                _hasAlphaProp = magickImage.GetProperty("HasAlpha");

                // For the TIFF "ExtraSamples = unspecified" workaround: we
                // need to read the tiff:alpha attribute after Read() and
                // negate the alpha channel if it's unspecified (matte
                // semantics) to get back to PNG-style alpha (opacity
                // semantics). All three pieces are optional — if missing
                // we just skip the fix instead of failing the whole encode.
                _getAttributeMethod = magickImage.GetMethod("GetAttribute", new[] { typeof(string) });
                _channelsType = FindTypeInMagickAssemblies("ImageMagick.Channels");
                if (_channelsType != null)
                    _negateChannelMethod = magickImage.GetMethod("Negate", new[] { _channelsType });

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
