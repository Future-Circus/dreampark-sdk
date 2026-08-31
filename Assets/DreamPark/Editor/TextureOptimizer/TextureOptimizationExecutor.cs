#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// Applies a list of approved <see cref="TexturePlanRow"/>s. For each
    /// row we:
    ///
    ///   1. Read the source via Magick.NET, downscale, re-encode to the
    ///      target format, write to a temp path next to the original.
    ///   2. Preserve the asset's GUID by carrying the original .meta file
    ///      to the new path before we run AssetDatabase.Refresh — this
    ///      keeps every material/prefab reference intact across the
    ///      extension change.
    ///   3. Delete the original source + its .meta (now stale).
    ///   4. Refresh the AssetDatabase so Unity re-imports the new file
    ///      under the carried-over GUID.
    ///   5. Apply the row's TextureImporter settings (max size, crunch,
    ///      compression quality) to the freshly imported texture.
    ///
    /// The GUID-preservation step is the linchpin. If we just write a
    /// new file and let Unity assign it a fresh GUID, every material in
    /// the project loses its texture reference. Carrying the .meta file
    /// across the rename is the only reliable way to keep references
    /// stable through a format change.
    ///
    /// The executor runs synchronously inside the editor. For a ~500-
    /// texture batch this typically takes 30-120 seconds depending on
    /// source dimensions; the UI shows a progress bar throughout.
    /// </summary>
    public static class TextureOptimizationExecutor
    {
        /// <summary>
        /// Apply every row of <paramref name="rows"/> that has
        /// <see cref="TexturePlanRow.WillBeModified"/> = true. Pass
        /// <paramref name="dryRun"/> = true to compute the result rows
        /// (with the real after-bytes from a temp re-encode) without
        /// committing anything to disk — useful for a "preview" button.
        /// </summary>
        public static ExecuteResult Apply(
            IList<TexturePlanRow> rows,
            bool dryRun,
            Action<float, string> onProgress = null)
        {
            var result = new ExecuteResult();

            // Defense-in-depth: the window's OnEnable triggers a
            // one-time auto-install, so Magick.NET should always be
            // ready by the time Apply is invoked. But if somebody calls
            // Apply programmatically (or a manual install was deleted
            // mid-session), fail loudly instead of silently doing nothing.
            if (!MagickNetBootstrap.IsAvailable)
            {
                EditorUtility.DisplayDialog(
                    "Texture Optimizer not ready",
                    "Magick.NET isn't loaded. Close and reopen the Texture Optimizer window — "
                    + "it auto-installs on first use.",
                    "OK");
                return result;
            }

            // Snapshot the work list so caller mutations during the run
            // don't desync us.
            var work = new List<TexturePlanRow>();
            foreach (var r in rows) if (r != null && r.WillBeModified) work.Add(r);

            // Stop Unity from doing its (very slow) asset-database refresh
            // after every File.Delete / File.Write — we batch the refresh
            // at the end via the StopAssetEditing block.
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    var row = work[i];
                    string label = Path.GetFileName(row.usage.assetPath);
                    onProgress?.Invoke((float)i / work.Count, $"{(dryRun ? "Previewing" : "Optimizing")}: {label}");

                    var rr = ApplyRow(row, dryRun);
                    result.rows.Add(rr);
                    result.processed++;
                    if (rr.ok) result.succeeded++; else result.failed++;
                    result.bytesBefore += rr.bytesBefore;
                    result.bytesAfter += rr.bytesAfter;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            // Pass 2 — apply importer settings now that Unity has re-imported
            // the new files. We have to do this AFTER refresh because the
            // TextureImporter object doesn't exist for the new path until
            // Unity has done its import pass. We pair plan rows with their
            // ExecuteRowResult so we apply settings to the file Pass 1
            // actually wrote (not the one the row started at — extensions
            // changed).
            if (!dryRun)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < work.Count; i++)
                    {
                        onProgress?.Invoke(0.9f + 0.1f * i / work.Count, "Applying importer settings...");
                        var rr = i < result.rows.Count ? result.rows[i] : null;
                        if (rr != null && rr.ok)
                            ApplyImporterSettings(work[i], rr.finalPath);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }
            }

            onProgress?.Invoke(1f, "Done.");
            return result;
        }

        // ─── Per-row execution ──────────────────────────────────────────

        private static ExecuteRowResult ApplyRow(TexturePlanRow row, bool dryRun)
        {
            var rr = new ExecuteRowResult
            {
                sourcePath = row.usage.assetPath,
                bytesBefore = row.usage.fileBytes,
            };

            try
            {
                string srcAssetPath = row.usage.assetPath;
                string srcAbs = Path.GetFullPath(srcAssetPath);
                if (!File.Exists(srcAbs))
                {
                    rr.error = "Source file missing at " + srcAbs;
                    Debug.LogError($"[TextureOptimizer] {row.usage.assetPath}: source not found at {srcAbs}. "
                                 + "AssetDatabase path may not match disk (case, special characters, or Unity reimport pending).");
                    return rr;
                }

                // Guardrail: the planner uses KeepAsIs as a "do not touch"
                // sentinel. The window also lets users pick it manually as a
                // per-row override. Either way, treat it as a no-op success.
                if (row.targetFormat == TargetFormat.KeepAsIs)
                {
                    rr.ok = true;
                    rr.finalPath = srcAssetPath;
                    rr.bytesAfter = row.usage.fileBytes;
                    return rr;
                }

                // Alpha reality check: planner picks PNG if Unity's
                // importer says alpha exists, but the importer only
                // reports the channel — not whether the channel
                // carries information. Magick.NET's histogram catches
                // "vendor exported with alpha but it's all 255" and we
                // downgrade to JPG for the size win.
                if (row.targetFormat == TargetFormat.PNG && row.usage.role == TextureRole.Albedo)
                {
                    if (!MagickNetBootstrap.HasMeaningfulAlpha(srcAbs))
                        row.targetFormat = TargetFormat.JPG;
                }

                string targetExt = row.targetFormat == TargetFormat.JPG ? ".jpg" : ".png";
                string baseNoExt = Path.Combine(
                    Path.GetDirectoryName(srcAssetPath),
                    Path.GetFileNameWithoutExtension(srcAssetPath));
                string newAssetPath = baseNoExt + targetExt;
                string tempAbs = Path.GetFullPath(baseNoExt + ".__txopt_tmp" + targetExt);

                // ── Step 1: encode to a temp path. ─────────────────────
                // We never write directly to the final path — if the
                // encoder throws halfway, the original file is still
                // intact.
                //
                // Routing:
                //   (a) TIFFs with ExtraSamples=unspecified → Unity path
                //       directly. ImageMagick's TIFF reader interprets the
                //       4th channel as matte (transparency) and inverts on
                //       PNG write — even with the Negate(Channels.Alpha)
                //       workaround in MagickNetBootstrap. Unity's
                //       TextureImporter reads these files correctly (proven
                //       by "alpha works in-game" on the imported texture),
                //       so we use its EncodeToPNG() and bypass Magick.NET's
                //       interpretation entirely.
                //   (b) Everything else → Magick.NET first (faster, Lanczos
                //       resize, direct file access). If it throws — usually
                //       a vendor TGA/TIF with a non-standard layout
                //       Magick.NET's reader refuses — fall back to
                //       UnityImageProcessor (slower but more permissive).
                bool routeToUnityFirst =
                    row.targetFormat == TargetFormat.PNG
                    && MagickNetBootstrap.IsTiffExtraSamplesUnspecified(srcAbs);

                if (routeToUnityFirst)
                {
                    Debug.Log(
                        $"[TextureOptimizer] {row.usage.assetPath}: routing through Unity's "
                        + "encoder (TIFF has ExtraSamples=unspecified — Magick.NET's interpretation "
                        + "would invert the alpha channel).");
                    UnityImageProcessor.ReEncode(
                        srcAssetPath,
                        tempAbs,
                        row.targetFormat,
                        row.targetMaxSize,
                        TextureOptimizationPlanner.JpgQuality);
                }
                else
                {
                    try
                    {
                        MagickNetBootstrap.ReEncode(
                            srcAbs,
                            tempAbs,
                            row.targetFormat,
                            row.targetMaxSize,
                            TextureOptimizationPlanner.JpgQuality);
                    }
                    catch (Exception magickError)
                    {
                        Debug.LogWarning(
                            $"[TextureOptimizer] {row.usage.assetPath}: Magick.NET rejected this file " +
                            $"({magickError.GetType().Name}: {magickError.Message}). " +
                            "Falling back to Unity's built-in encoder.");
                        // Clean any half-written temp from the failed Magick
                        // attempt before retrying.
                        try { if (File.Exists(tempAbs)) File.Delete(tempAbs); } catch { /* ignore */ }

                        UnityImageProcessor.ReEncode(
                            srcAssetPath,           // Unity path needs the Assets-relative form
                            tempAbs,
                            row.targetFormat,
                            row.targetMaxSize,
                            TextureOptimizationPlanner.JpgQuality);
                    }
                }

                long afterBytes = new FileInfo(tempAbs).Length;
                rr.bytesAfter = afterBytes;
                rr.finalPath = newAssetPath;

                if (dryRun)
                {
                    // Don't commit; clean up the temp.
                    try { File.Delete(tempAbs); } catch { /* ignore */ }
                    rr.ok = true;
                    return rr;
                }

                // ── Step 2: carry the original .meta to the new path. ──
                // This is what preserves the GUID. Unity will accept the
                // .meta as authoritative when it imports the new file on
                // the next Refresh.
                string srcMeta = srcAbs + ".meta";
                string newAbs = Path.GetFullPath(newAssetPath);
                string newMeta = newAbs + ".meta";

                // ── Step 3: commit. Same-extension case → just overwrite.
                //   Cross-extension case → write temp, delete old, write meta.
                bool sameExtension = string.Equals(
                    Path.GetExtension(srcAssetPath),
                    targetExt,
                    StringComparison.OrdinalIgnoreCase);

                if (sameExtension)
                {
                    // Same extension: replace contents in place.
                    File.Copy(tempAbs, srcAbs, overwrite: true);
                    File.Delete(tempAbs);
                    rr.finalPath = srcAssetPath; // unchanged
                }
                else
                {
                    // Different extension: this is the GUID-preservation
                    // path. Before we destroy anything, check for a
                    // pre-existing collision — if a sibling Bar.png
                    // already exists next to Bar.tga, blindly overwriting
                    // it would silently delete hand-authored content.
                    // Refuse the row instead.
                    if (File.Exists(newAbs))
                    {
                        File.Delete(tempAbs);
                        rr.error = $"Target file already exists: {newAssetPath}. "
                                 + "Move or delete it manually, then re-run the optimizer.";
                        return rr;
                    }
                    if (File.Exists(newMeta))
                    {
                        File.Delete(tempAbs);
                        rr.error = $"Target meta file already exists: {newAssetPath}.meta. "
                                 + "Move or delete it manually, then re-run the optimizer.";
                        return rr;
                    }

                    // Move temp into place, then carry the .meta across
                    // (preserves the GUID, which is the whole point of
                    // this dance). Finally delete the original source.
                    File.Move(tempAbs, newAbs);
                    if (File.Exists(srcMeta))
                        File.Move(srcMeta, newMeta);
                    File.Delete(srcAbs);
                }

                rr.ok = true;
                return rr;
            }
            catch (Exception e)
            {
                rr.error = e.Message;
                Debug.LogError($"[TextureOptimizer] Failed to optimize {row.usage.assetPath}: {e}");
                return rr;
            }
        }

        // ─── Importer settings pass ─────────────────────────────────────

        private static void ApplyImporterSettings(TexturePlanRow row, string finalAssetPath)
        {
            // finalAssetPath is whatever Pass 1 actually wrote — the same
            // as the source for in-place re-encodes, or a renamed file
            // for cross-extension cases. Trusting the result avoids the
            // path-reconstruction bugs you get when you try to recompute
            // the extension here (case sensitivity, format mismatches).
            string path = string.IsNullOrEmpty(finalAssetPath) ? row.usage.assetPath : finalAssetPath;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            importer.maxTextureSize = row.targetImporterMaxSize;
            importer.crunchedCompression = row.useCrunchCompression
                && row.usage.role != TextureRole.Normal;   // crunched normals look bad
            if (importer.crunchedCompression)
                importer.compressionQuality = Mathf.Clamp(row.crunchQuality, 0, 100);

            // Default platform settings — let Quest/Android pick a sensible
            // compressed format (ASTC 6x6 is the modern Quest default).
            var defaultSettings = importer.GetDefaultPlatformTextureSettings();
            defaultSettings.maxTextureSize = row.targetImporterMaxSize;
            defaultSettings.crunchedCompression = importer.crunchedCompression;
            importer.SetPlatformTextureSettings(defaultSettings);

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
    }
}
#endif
