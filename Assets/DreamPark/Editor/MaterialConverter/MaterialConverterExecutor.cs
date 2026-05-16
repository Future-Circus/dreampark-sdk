#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using DreamPark.EditorTools;  // for MaterialConverter

namespace DreamPark.EditorTools.MaterialConversion
{
    /// <summary>
    /// Runs an approved plan against the project. Thin wrapper around the
    /// existing static MaterialConverter API:
    ///
    ///   ConvertOpaqueToUniversal → MaterialConverter.ConvertMaterial
    ///   ConvertOpaqueToUnlit     → MaterialConverter.ConvertMaterialToUnlit
    ///   ConvertParticle          → MaterialConverter.ConvertParticleMaterial
    ///
    /// All conversions mutate the .mat in place (GUID preserved, references
    /// in prefabs / scenes stay valid). We call AssetDatabase.SaveAssets()
    /// once at the end rather than per-row — Unity gets cranky and slow if
    /// you SaveAssets inside a tight loop over hundreds of materials.
    /// </summary>
    public static class MaterialConverterExecutor
    {
        public static MaterialExecuteResult Apply(IList<MaterialPlanRow> rows, System.Action<float, string> onProgress = null)
        {
            var result = new MaterialExecuteResult();
            if (rows == null || rows.Count == 0) return result;

            // ── Pre-pass: capture which materials had explicit flipbook
            // intent BEFORE we mutate them. The post-convert renderer
            // refresh needs this to know whether to auto-enable Unity's
            // TextureSheetAnimation. We can't infer flipbook intent from
            // the converted material because changing the shader drops
            // _MotionVector and other vendor-specific signals.
            //
            // Without this gate, the auto-enable fires on any material
            // whose ParticleSystem happens to have numTilesX/Y > 1 set up
            // — including materials where those tile counts are stale
            // authoring leftovers (e.g. CFXR's CFXM2_Glow), causing the
            // single-image texture to render cut/stretched.
            var flipbookIntentPaths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null || !row.WillBeModified) continue;
                if (row.kind != MaterialConvertKind.ConvertParticle) continue;
                if (string.IsNullOrEmpty(row.usage?.assetPath)) continue;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(row.usage.assetPath);
                if (mat == null) continue;
                if (HadFlipbookIntent(mat))
                    flipbookIntentPaths.Add(row.usage.assetPath);
            }

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if ((i & 7) == 0)
                    onProgress?.Invoke((float)i / rows.Count, $"Converting {System.IO.Path.GetFileName(row.usage.assetPath)}");

                if (!row.WillBeModified)
                {
                    result.skipped++;
                    continue;
                }

                var mat = AssetDatabase.LoadAssetAtPath<Material>(row.usage.assetPath);
                if (mat == null)
                {
                    result.failed++;
                    result.rows.Add(new MaterialExecuteRowResult {
                        materialPath = row.usage.assetPath,
                        kind = row.kind,
                        fromShader = row.usage.shaderName,
                        toShader = row.targetShader,
                        ok = false,
                        error = "Failed to load material asset",
                    });
                    continue;
                }

                string fromShader = mat.shader != null ? mat.shader.name : "(none)";
                bool ok = false;
                string error = null;
                try
                {
                    switch (row.kind)
                    {
                        case MaterialConvertKind.ConvertOpaqueToUniversal:
                            ok = MaterialConverter.ConvertMaterial(mat);
                            break;
                        case MaterialConvertKind.ConvertOpaqueToUnlit:
                            ok = MaterialConverter.ConvertMaterialToUnlit(mat);
                            break;
                        case MaterialConvertKind.ConvertParticle:
                            ok = MaterialConverter.ConvertParticleMaterial(mat);
                            break;
                        default:
                            ok = false;
                            error = $"Unsupported kind: {row.kind}";
                            break;
                    }
                }
                catch (System.Exception e)
                {
                    ok = false;
                    error = e.Message;
                    Debug.LogError($"[MaterialConverter] Failed to convert '{row.usage.assetPath}': {e}");
                }

                result.processed++;
                if (ok) result.converted++;
                else    result.failed++;

                result.rows.Add(new MaterialExecuteRowResult {
                    materialPath = row.usage.assetPath,
                    kind = row.kind,
                    fromShader = fromShader,
                    toShader = mat.shader != null ? mat.shader.name : row.targetShader,
                    ok = ok,
                    error = error,
                });
            }

            AssetDatabase.SaveAssets();

            // Post-pass: any prefab that uses a freshly-converted particle
            // material likely has its ParticleSystemRenderer configured for
            // the vendor shader's Custom Vertex Streams. Hovl Studio and
            // some CFXR materials need 5+ streams for motion-vector flipbook
            // blending; DreamPark/Particles uses the standard 4 streams.
            // Without resetting, the renderer keeps writing vendor-shaped
            // data to TEXCOORD0, which our shader misinterprets as
            // "whole-sheet UVs" → the user sees every flipbook frame at
            // once instead of one frame at a time. Auto-fix.
            onProgress?.Invoke(0.95f, "Refreshing particle renderers...");
            result.rendererStreamsReset = RefreshParticleSystemRenderers(rows, flipbookIntentPaths);
            if (result.rendererStreamsReset > 0)
            {
                Debug.Log($"[MaterialConverter] Reset Custom Vertex Streams on " +
                          $"{result.rendererStreamsReset} ParticleSystemRenderer(s) across " +
                          $"the affected prefabs (auto-fix for flipbook UVs).");
            }

            onProgress?.Invoke(1f, "Done");
            return result;
        }

        // Walk every prefab that uses a freshly-converted particle material
        // and reset any ParticleSystemRenderer's Custom Vertex Streams to
        // the default set (Position, Normal, Color, UV). Returns the count
        // of renderers actually touched (not prefabs — a single prefab may
        // have multiple particle systems).
        //
        // Why a separate pass: PrefabUtility.LoadPrefabContents +
        // SaveAsPrefabAsset is the only safe way to mutate a prefab asset
        // from script. We do it in batch after material conversion is
        // done so a single prefab gets opened/saved once even if multiple
        // of its renderers reference different converted materials.
        public static int RefreshParticleSystemRenderers(
            IList<MaterialPlanRow> rows,
            HashSet<string> flipbookIntentPaths = null)
        {
            if (rows == null || rows.Count == 0) return 0;
            flipbookIntentPaths ??= new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            // Set of just-converted PARTICLE material asset paths. Only
            // particle conversions need the streams reset — opaque rows
            // (Universal / Unlit) target shaders that don't care about
            // ParticleSystem's vertex-stream config.
            var convertedParticleMaterials = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null || !row.WillBeModified) continue;
                if (row.kind != MaterialConvertKind.ConvertParticle) continue;
                if (string.IsNullOrEmpty(row.usage?.assetPath)) continue;
                convertedParticleMaterials.Add(row.usage.assetPath);
            }
            if (convertedParticleMaterials.Count == 0) return 0;

            // Collect every prefab path that touches any of those materials.
            // We rely on the usage-graph data the planner already computed —
            // saves us from re-walking the project.
            var affectedPrefabs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row.kind != MaterialConvertKind.ConvertParticle) continue;
                if (row.usage?.usingPrefabs == null) continue;
                foreach (var p in row.usage.usingPrefabs)
                    affectedPrefabs.Add(p);
            }
            if (affectedPrefabs.Count == 0) return 0;

            // The default stream set DreamPark/Particles is authored
            // against. Matches what a fresh ParticleSystem would write
            // before any vendor shader installer added custom streams.
            var defaultStreams = new List<ParticleSystemVertexStream>
            {
                ParticleSystemVertexStream.Position,
                ParticleSystemVertexStream.Normal,
                ParticleSystemVertexStream.Color,
                ParticleSystemVertexStream.UV,
            };

            int renderersTouched = 0;
            var currentStreams = new List<ParticleSystemVertexStream>(8);

            foreach (var prefabPath in affectedPrefabs)
            {
                // LoadPrefabContents opens the prefab into an in-memory
                // scene that we can mutate freely. Save back via
                // SaveAsPrefabAsset. ALWAYS pair with UnloadPrefabContents
                // (try/finally) so we don't leak the temp scene if
                // anything throws.
                GameObject contents = null;
                try
                {
                    contents = PrefabUtility.LoadPrefabContents(prefabPath);
                    if (contents == null) continue;

                    bool prefabDirty = false;
                    var renderers = contents.GetComponentsInChildren<ParticleSystemRenderer>(includeInactive: true);
                    foreach (var r in renderers)
                    {
                        if (r == null) continue;

                        // Does this renderer use any of the materials we
                        // just converted? Check both sharedMaterials and
                        // trailMaterial (Trails module material slot).
                        bool usesConverted = false;
                        var mats = r.sharedMaterials;
                        if (mats != null)
                        {
                            for (int i = 0; i < mats.Length; i++)
                            {
                                if (mats[i] == null) continue;
                                string mp = AssetDatabase.GetAssetPath(mats[i]);
                                if (!string.IsNullOrEmpty(mp) && convertedParticleMaterials.Contains(mp))
                                {
                                    usesConverted = true;
                                    break;
                                }
                            }
                        }
                        if (!usesConverted && r.trailMaterial != null)
                        {
                            string tp = AssetDatabase.GetAssetPath(r.trailMaterial);
                            if (!string.IsNullOrEmpty(tp) && convertedParticleMaterials.Contains(tp))
                                usesConverted = true;
                        }
                        if (!usesConverted) continue;

                        bool rendererDirty = false;

                        // ── Fix A: Custom Vertex Streams ─────────────────
                        // Reset to default when the renderer is feeding the
                        // shader a vendor-shaped stream layout. Catches
                        // motion-vector flipbook setups (which DreamPark/
                        // Particles doesn't read) and any other vendor
                        // shader's custom stream packing.
                        currentStreams.Clear();
                        r.GetActiveVertexStreams(currentStreams);
                        bool streamsMatch = currentStreams.Count == defaultStreams.Count;
                        if (streamsMatch)
                        {
                            for (int i = 0; i < currentStreams.Count; i++)
                            {
                                if (currentStreams[i] != defaultStreams[i])
                                {
                                    streamsMatch = false;
                                    break;
                                }
                            }
                        }
                        if (!streamsMatch)
                        {
                            r.SetActiveVertexStreams(defaultStreams);
                            rendererDirty = true;
                        }

                        // ── Fix B: Enable Unity's TextureSheetAnimation ──
                        // Vendor packs like Hovl Studio implement flipbook
                        // inside their shader using time-based UV remap,
                        // and leave Unity's UVModule (TextureSheetAnimation)
                        // DISABLED — even though it has the tile counts
                        // configured (e.g. 8x8). DreamPark/Particles doesn't
                        // do shader-side flipbook; it samples _BaseMap at
                        // whatever UVs the renderer feeds. If the UVModule
                        // is configured-but-disabled, enabling it makes
                        // Unity remap UV0 per-frame so our shader gets the
                        // right sub-rect — fixing the "whole flipbook
                        // visible at once" failure mode.
                        //
                        // GATING: only auto-enable when the source material
                        // had explicit flipbook intent (motion-vector
                        // texture or _FLIPBOOKBLENDING_ON keyword). Tile
                        // counts > 1 alone aren't enough — many vendor
                        // materials (CFXR's CFXM2_Glow, for example) have
                        // stale numTilesX/Y leftover from authoring even
                        // when the material is a single-image glow. We
                        // flipbook-intent check prevents auto-enabling on
                        // those false positives.
                        bool rendererHasFlipbookIntent = false;
                        if (mats != null)
                        {
                            for (int i = 0; i < mats.Length; i++)
                            {
                                if (mats[i] == null) continue;
                                string mp = AssetDatabase.GetAssetPath(mats[i]);
                                if (!string.IsNullOrEmpty(mp) && flipbookIntentPaths.Contains(mp))
                                {
                                    rendererHasFlipbookIntent = true;
                                    break;
                                }
                            }
                        }

                        var ps = r.GetComponent<ParticleSystem>();
                        if (ps != null && rendererHasFlipbookIntent)
                        {
                            var tsa = ps.textureSheetAnimation;
                            if (!tsa.enabled && (tsa.numTilesX > 1 || tsa.numTilesY > 1))
                            {
                                tsa.enabled = true;

                                // Vendor packs that drove flipbook from the
                                // shader typically leave Unity's frameOverTime
                                // curve flat at zero (because Unity wasn't
                                // expected to do the animation). Once we
                                // enable the module, that flat curve would
                                // freeze the particle on frame 0. Set a
                                // linear 0→1 curve so it plays through the
                                // whole sheet over the particle's lifetime —
                                // matches Hovl's authoring intent.
                                var linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                                tsa.frameOverTime = new ParticleSystem.MinMaxCurve(1f, linear);

                                // Cycle once over lifetime (default Hovl
                                // setup is also cycleCount=1).
                                tsa.cycleCount = Mathf.Max(1, tsa.cycleCount);

                                rendererDirty = true;
                                Debug.Log($"[MaterialConverter] Enabled TextureSheetAnimation " +
                                          $"({tsa.numTilesX}x{tsa.numTilesY}, linear 0→1 over lifetime) " +
                                          $"on '{ps.name}' in '{prefabPath}'.");
                            }
                        }

                        if (rendererDirty)
                        {
                            renderersTouched++;
                            prefabDirty = true;
                        }
                    }

                    if (prefabDirty)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[MaterialConverter] Failed to refresh particle renderers " +
                                   $"in '{prefabPath}': {e.Message}");
                }
                finally
                {
                    if (contents != null)
                        PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            return renderersTouched;
        }

        // Detect whether a source material was authored with flipbook intent —
        // i.e. it had motion-vector flipbook signals that we approximate by
        // enabling Unity's TextureSheetAnimation. Must be called BEFORE
        // ConvertParticleMaterial runs, because switching the shader to
        // DreamPark/Particles drops vendor-specific properties like
        // _MotionVector.
        private static bool HadFlipbookIntent(Material mat)
        {
            if (mat == null) return false;
            // Motion-vector flipbook texture — strongest signal. If the
            // vendor authored a flipbook motion-vector map, they meant for
            // the material to play a flipbook.
            if (mat.HasProperty("_MotionVector") && mat.GetTexture("_MotionVector") != null)
                return true;
            // URP's _FLIPBOOKBLENDING_ON keyword (the actual name URP
            // ships) AND the older spelling.
            if (mat.IsKeywordEnabled("_FLIPBOOKBLENDING_ON")) return true;
            if (mat.IsKeywordEnabled("_FLIPBOOK_BLENDING")) return true;
            // URP also exposes a _FlipbookBlending float toggle on some
            // shader-graph particles.
            if (mat.HasProperty("_FlipbookBlending") && mat.GetFloat("_FlipbookBlending") > 0.5f)
                return true;
            return false;
        }
    }
}
#endif
