#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using DreamPark.EditorTools;
using DreamPark.EditorTools.MaterialConversion;

namespace DreamPark.PreUploadChecks.Checks
{
    // Materials on mesh renderers whose shader does not integrate Meta environment
    // occlusion.
    //
    // WHY THIS BLOCKS
    //
    // From DreamPark-Particles.shader's own header:
    //
    //   "Meta passthrough environment occlusion (HARD_OCCLUSION / SOFT_OCCLUSION) —
    //    particles are clipped/faded by REAL-WORLD geometry on Quest 3 via Meta's
    //    Depth API. Driven by the standard keyword that Meta's OcclusionToggle /
    //    EnvironmentDepthOcclusion components set globally; no per-material work
    //    needed. When neither keyword is enabled the Meta macros expand to no-ops
    //    (zero runtime cost)."
    //
    // A shader without it renders at full opacity regardless of real-world depth, so
    // virtual geometry draws straight over the guest's hands, furniture and walls.
    // There is no way to fix that from outside the content: it needs a re-upload.
    //
    // THE MOST IMPORTANT THING TO KNOW ABOUT THIS CHECK
    //
    // Occlusion is NOT a per-material property. HARD_OCCLUSION / SOFT_OCCLUSION are
    // GLOBAL keywords set at runtime by Meta's EnvironmentDepthManager /
    // OcclusionToggle. Material.IsKeywordEnabled and material.enabledKeywords are
    // therefore useless here — MaterialConverter never writes an occlusion keyword,
    // and nothing else does either. The test must be SHADER-level, always.
    //
    // Three states, never two. A shader that errored, lives in an unreadable package,
    // or whose subgraph GUID doesn't resolve is Unknown, and Unknown is reported as a
    // Warning. Blocking on "I couldn't tell" is exactly how a gate teaches people to
    // click through it.
    public sealed class MetaOcclusionCheck : IPreUploadCheck
    {
        public const string CheckId = "meta-occlusion";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Materials without Meta Occlusion"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }

        // NOT in the advisory pass. It loads every content root's prefab contents and,
        // on a cache miss, recursively reads shader source off disk. The advisory scan
        // fires on every projectChanged — i.e. every asset save while the uploader is
        // open — and paying that there froze the editor for seconds at a time.
        public bool RunsInAdvisoryScan { get { return false; } }

        public string Rationale
        {
            get
            {
                return "Without environment occlusion, virtual geometry draws on top of the guest's "
                     + "hands, furniture and walls on Quest. Only materials on MeshRenderers and "
                     + "SkinnedMeshRenderers inside this package's prefabs are checked — content ships "
                     + "as prefabs, so scene-only materials are not covered.";
            }
        }

        // Meta's OcclusionSubGraph, from com.meta.xr.sdk.core. Verified present in the
        // pinned 81.0.0 package and referenced by both DreamPark shader graphs — but
        // resolved at scan time rather than trusted, because it is package-version
        // dependent. If it fails to resolve we fall back to the node's display name,
        // and if that fails too the answer is Unknown, never "missing".
        private const string OcclusionSubGraphGuid = "04fdbd8a7b3535c4a9d6c02a8763787e";
        private const string OcclusionSubGraphName = "OcclusionSubGraph";

        // Hand-written shaders integrate through Meta's URP header.
        private static readonly string[] SourceMarkers =
        {
            "com.meta.xr.sdk.core/Shaders/EnvironmentDepth",
            "EnvironmentOcclusionURP.hlsl",
            "META_DEPTH_OCCLUDE_OUTPUT",
            "META_DEPTH_INITIALIZE_VERTEX_OUTPUT",
            "HARD_OCCLUSION",
            "SOFT_OCCLUSION",
        };

        // WHAT IS DELIBERATELY NOT REPORTED
        //
        // Everything under Assets/DreamPark/ is SDK-owned foundation, and this check is
        // the only one that wasn't already treating it that way — SmartBundleGrouper's
        // FoundationPathPrefixes and OutsideContentFolderCheck both do. Two reasons it
        // matters more here than anywhere else:
        //
        //  1. A creator CANNOT fix an SDK material. Blocking their upload on one gives
        //     them a red gate with no action behind it, which is precisely how a gate
        //     stops being read. (LuaSurfaceGate's header: "A modal with an 'Upload
        //     anyway' button is a scarce resource… That already happened once here.")
        //
        //  2. Most SDK shaders have no occlusion ON PURPOSE. Assets/DreamPark/Shaders/
        //     holds DepthMask (produces depth, isn't occluded by it), InvisibleOccluder
        //     (z-only), KeepAliveObject (deliberate depth/colour ping), NormalOverlay
        //     (editor debug), and the screen surfaces LavaScreen and BrokenScreen —
        //     BrokenScreen is Queue=Transparent with refraction and a vignette, i.e. a
        //     screen overlay, not world geometry. Occluding a UI surface against
        //     real-world depth is the bug, not the fix.
        //
        // The three that DO integrate occlusion — DreamPark-UniversalShader,
        // DreamPark-Unlit, DreamPark/Particles — pass Tier 1 on the allowlist anyway,
        // so this exemption costs no real coverage.
        //
        // Path-based rather than a name list: a name list drifts the moment someone adds
        // a shader, and this one already had to be hand-extended once.
        private const string SdkPrefix = "Assets/DreamPark/";

        // Non-DreamPark shaders that are occluders or depth producers by design. These
        // live in Packages/, so the SDK path rule doesn't reach them.
        private static readonly HashSet<string> IntentionallyExempt = new HashSet<string>(StringComparer.Ordinal)
        {
            "Meta/EnvironmentDepth/DepthMask",                      // produces depth, isn't occluded by it
            "Meta/MRUK/MixedReality/InvisibleOccluderCulled",       // z-only geometric occluder
        };

        private static bool IsSdkOwned(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                && assetPath.StartsWith(SdkPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private enum Support { Supported, Missing, Unknown }

        // Keyed by shader ASSET GUID, not instance id: a reimported shader usually
        // keeps its instance id, so an instance-id cache would hand back the stale
        // verdict for a shader the developer just fixed and leave the upload blocked
        // with no way out short of restarting Unity.
        private static readonly Dictionary<string, Support> shaderCache =
            new Dictionary<string, Support>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> fileScanCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Called by the runner before any re-run. Both caches only ever store
        // positives / verdicts, so without this a shader that GAINED occlusion still
        // reports Missing, and one that LOST it still reports clean.
        public static void InvalidateShaderCaches()
        {
            shaderCache.Clear();
            fileScanCache.Clear();
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();

            // material asset path → the roots that use it on a mesh renderer
            var meshMaterials = new Dictionary<string, List<ContentRootInfo>>(StringComparer.OrdinalIgnoreCase);
            var usingPrefabs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            int i = 0;
            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count) * 0.6f,
                             $"Collecting materials in {root.name}…");
                i++;

                try
                {
                    CollectMeshMaterials(root, meshMaterials, usingPrefabs);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not collect materials from {root.assetPath}: {e.Message}");
                }
            }

            int j = 0;
            foreach (var kv in meshMaterials)
            {
                ctx.Progress(0.6f + (float)j / Mathf.Max(1, meshMaterials.Count) * 0.4f,
                             "Checking shaders for Meta occlusion…");
                j++;

                string matPath = kv.Key;
                var users = kv.Value;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) continue;

                var shader = mat.shader;
                string shaderName = shader != null ? shader.name : "(none)";

                if (IntentionallyExempt.Contains(shaderName)) continue;

                // SDK-owned material, or a creator material still pointing at an SDK
                // shader. Either way the occlusion decision belongs to the SDK, not to
                // the person trying to upload an attraction. See SdkPrefix above.
                if (IsSdkOwned(matPath)) continue;
                if (shader != null && IsSdkOwned(AssetDatabase.GetAssetPath(shader))) continue;

                Support support = Evaluate(shader);
                if (support == Support.Supported) continue;

                // A material embedded in an FBX is ReadOnlyEmbedded and cannot be
                // converted in place. Offering a Convert button that silently no-ops
                // is worse than saying so.
                bool embedded = !matPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);

                var owner = users.FirstOrDefault();
                string userList = string.Join(", ", users.Select(u => u.name).Distinct().Take(6));

                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = support == Support.Missing ? CheckSeverity.Blocking : CheckSeverity.Warning,
                    assetGuid = owner != null ? owner.guid : AssetDatabase.AssetPathToGUID(matPath),
                    assetPath = owner != null ? owner.assetPath : matPath,
                    subKey = matPath,
                    title = support == Support.Missing
                        ? $"{Path.GetFileNameWithoutExtension(matPath)} — shader '{shaderName}' has no Meta occlusion"
                        : $"{Path.GetFileNameWithoutExtension(matPath)} — could not verify occlusion on '{shaderName}'",
                    detail = support == Support.Missing
                        ? $"{matPath}\nUsed by: {userList}\n\n"
                        + "This material will render at full opacity regardless of real-world depth, so "
                        + "it draws over the guest's hands, furniture and walls."
                        + (embedded ? "\n\nThis material is embedded in a model file and cannot be converted "
                                    + "in place — run Extract Materials on the source asset first." : "")
                        : $"{matPath}\nUsed by: {userList}\n\n"
                        + "The shader could not be read (compile error, unreadable package, or an "
                        + "unresolvable subgraph reference), so this is reported rather than blocked. "
                        + "Verify by hand.",
                };

                if (support == Support.Missing && !embedded)
                {
                    string capturedPath = matPath;
                    List<string> prefabs;
                    usingPrefabs.TryGetValue(matPath, out prefabs);
                    var capturedPrefabs = prefabs ?? new List<string>();

                    finding.fixes.Add(new FixAction(
                        "Convert to DreamPark shader",
                        () => Convert(capturedPath, capturedPrefabs))
                    {
                        tooltip = "Runs the existing MaterialConverter, preserving textures and "
                                + "colour/scalar values.",
                        confirmTitle = "Convert material",
                        confirmMessage =
                            $"Convert {Path.GetFileName(capturedPath)} to a DreamPark shader?\n\n"
                          + "Textures and colour/scalar values are remapped by alias. GUIDs are "
                          + "preserved — prefab references stay valid.\n\n"
                          + "Conversion is LOSSY for anything the DreamPark shaders don't replicate: "
                          + "screen-space refraction, gradient/LUT remapping, custom channel masks, "
                          + "per-axis UV scrolling, rim/fresnel. Vector and int properties are not "
                          + "carried across at all.\n\n"
                          + "Cannot be undone via Ctrl-Z. Use version control to revert.",
                    });
                }

                // Navigation last: the actionable fix should be the leftmost button,
                // and DrawSectionBulkAction keys its batch button off fixes[0].
                string pingPath = matPath;
                finding.fixes.Add(FixAction.Navigate("Select material", () =>
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(pingPath);
                    if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
                }));

                findings.Add(finding);
            }

            return CheckResult.From(CheckId, findings);
        }

        // ------------------------------------------------------------------
        // Scoping: mesh renderers only
        //
        // MaterialUsageGraph deliberately over-collects — its own comment says
        // GetDependencies "follows Renderer.sharedMaterials, UI Image.material,
        // ParticleSystemRenderer materials, etc. without us having to know every
        // component type" — so it cannot answer "is this on an actual mesh?".
        // LineRenderer, TrailRenderer, SpriteRenderer and BillboardRenderer all derive
        // from Renderer but NOT from MeshRenderer, so a positive type test excludes
        // them cleanly.

        private static void CollectMeshMaterials(
            ContentRootInfo root,
            Dictionary<string, List<ContentRootInfo>> meshMaterials,
            Dictionary<string, List<string>> usingPrefabs)
        {
            GameObject go = PrefabUtility.LoadPrefabContents(root.assetPath);
            if (go == null) return;

            try
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;

                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        string mp = AssetDatabase.GetAssetPath(m);
                        if (string.IsNullOrEmpty(mp)) continue;
                        if (ContentRootScanner.IsThirdPartyLocal(mp)) continue;

                        List<ContentRootInfo> users;
                        if (!meshMaterials.TryGetValue(mp, out users))
                            meshMaterials[mp] = users = new List<ContentRootInfo>();
                        if (!users.Contains(root)) users.Add(root);

                        List<string> prefabs;
                        if (!usingPrefabs.TryGetValue(mp, out prefabs))
                            usingPrefabs[mp] = prefabs = new List<string>();
                        if (!prefabs.Contains(root.assetPath)) prefabs.Add(root.assetPath);
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(go);
            }
        }

        // ------------------------------------------------------------------
        // Detection: three tiers, short-circuiting, memoised per shader.

        private static Support Evaluate(Shader shader)
        {
            if (shader == null) return Support.Unknown;

            string key = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader));
            if (string.IsNullOrEmpty(key)) key = shader.name ?? "";

            Support cached;
            if (shaderCache.TryGetValue(key, out cached)) return cached;

            Support result = EvaluateUncached(shader);
            shaderCache[key] = result;
            return result;
        }

        private static Support EvaluateUncached(Shader shader)
        {
            // TIER 1 — allowlist. Zero I/O, covers effectively all converted content.
            try
            {
                if (DreamParkShaderNames.IsDreamParkShader(shader.name)) return Support.Supported;
            }
            catch { }

            // TIER 2 — compiled keyword space.
            //
            // NOTE: Unity documents keywordSpace as covering keywords declared in the
            // source file, keywords from dependencies (defined narrowly as Fallback
            // and UsePass), and Unity's predefined keywords. #include'd .hlsl files and
            // Shader Graph subgraphs are NOT named, so this tier is opportunistic: a
            // hit is trustworthy, a miss proves nothing and falls through to Tier 3.
            try
            {
                if (!ShaderUtil.ShaderHasError(shader))
                {
                    var names = shader.keywordSpace.keywordNames;
                    if (names != null && names.Any(n => n == "HARD_OCCLUSION" || n == "SOFT_OCCLUSION"))
                        return Support.Supported;
                }
            }
            catch { }

            // TIER 3 — recursive source scan. Authoritative, and the thing that lets
            // the finding explain itself.
            try
            {
                string path = AssetDatabase.GetAssetPath(shader);
                if (string.IsNullOrEmpty(path)) return Support.Unknown;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool? found = ScanShaderFile(path, visited, 0);

                if (found == true) return Support.Supported;
                if (found == false) return Support.Missing;
                return Support.Unknown;
            }
            catch
            {
                return Support.Unknown;
            }
        }

        // true = occlusion found; false = definitively absent; null = couldn't tell.
        private static bool? ScanShaderFile(string assetPath, HashSet<string> visited, int depth)
        {
            if (depth > 8) return null;
            if (string.IsNullOrEmpty(assetPath)) return null;
            // null, not false. `false` means "definitively has no occlusion", and
            // returning that for a file we merely already walked lets a genuinely
            // UNREADABLE file (which returned null the first time) come back as a
            // definitive negative on the second reach — turning "I couldn't tell" into
            // a Blocking finding, which is the exact failure the three-state design
            // exists to prevent.
            if (!visited.Add(assetPath)) return null;

            bool cachedHit;
            if (fileScanCache.TryGetValue(assetPath, out cachedHit) && cachedHit) return true;

            string text;
            try
            {
                // Works for "Assets/..." and for read-only "Packages/..." virtual paths.
                text = File.ReadAllText(assetPath);
            }
            catch
            {
                return null;    // unreadable → Unknown, never "missing"
            }

            foreach (var marker in SourceMarkers)
            {
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fileScanCache[assetPath] = true;
                    return true;
                }
            }

            string ext = Path.GetExtension(assetPath).ToLowerInvariant();

            if (ext == ".shadergraph" || ext == ".shadersubgraph")
            {
                // A .shadergraph is a STREAM of concatenated top-level JSON objects,
                // not one document — JsonUtility cannot parse it. The subgraph GUID
                // appears escaped inside m_SerializedSubGraph, so a raw substring
                // search finds it.
                if (text.IndexOf(OcclusionSubGraphGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fileScanCache[assetPath] = true;
                    return true;
                }
                if (text.IndexOf(OcclusionSubGraphName, StringComparison.Ordinal) >= 0)
                {
                    fileScanCache[assetPath] = true;
                    return true;
                }

                // Recurse into referenced subgraphs — occlusion may be one level down.
                bool sawUnresolvable = false;
                foreach (var guid in ExtractGuids(text))
                {
                    string sub = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(sub)) continue;

                    string subExt = Path.GetExtension(sub).ToLowerInvariant();
                    if (subExt != ".shadersubgraph" && subExt != ".shadergraph"
                        && subExt != ".hlsl" && subExt != ".cginc") continue;

                    if (!File.Exists(sub)) { sawUnresolvable = true; continue; }

                    bool? r = ScanShaderFile(sub, visited, depth + 1);
                    if (r == true) { fileScanCache[assetPath] = true; return true; }
                    if (r == null) sawUnresolvable = true;
                }

                return sawUnresolvable ? (bool?)null : false;
            }

            // Hand-written ShaderLab / HLSL: follow #include chains.
            bool sawUnreadableInclude = false;
            foreach (var include in ExtractIncludes(text))
            {
                string resolved = ResolveInclude(include, assetPath);
                if (string.IsNullOrEmpty(resolved)) { sawUnreadableInclude = true; continue; }

                bool? r = ScanShaderFile(resolved, visited, depth + 1);
                if (r == true) { fileScanCache[assetPath] = true; return true; }
                if (r == null) sawUnreadableInclude = true;
            }

            return sawUnreadableInclude ? (bool?)null : false;
        }

        private static readonly System.Text.RegularExpressions.Regex GuidPattern =
            new System.Text.RegularExpressions.Regex(
                @"\b[0-9a-fA-F]{32}\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static IEnumerable<string> ExtractGuids(string json)
        {
            // Any 32-hex run. A false positive costs one GUIDToAssetPath lookup that
            // returns nothing, so precision matters less than not missing a real
            // subgraph reference.
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in GuidPattern.Matches(json))
                found.Add(m.Value);
            return found;
        }

        private static IEnumerable<string> ExtractIncludes(string text)
        {
            var results = new List<string>();
            int idx = 0;
            while ((idx = text.IndexOf("#include", idx, StringComparison.Ordinal)) >= 0)
            {
                int q1 = text.IndexOf('"', idx);
                if (q1 < 0) break;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) break;

                results.Add(text.Substring(q1 + 1, q2 - q1 - 1));
                idx = q2 + 1;
            }
            return results;
        }

        private static string ResolveInclude(string include, string fromAssetPath)
        {
            if (string.IsNullOrEmpty(include)) return null;

            // Project- or package-relative.
            if (include.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || include.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return File.Exists(include) ? include : null;

            // Relative to the including file.
            string dir = Path.GetDirectoryName(fromAssetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir)) return null;

            string candidate = Path.Combine(dir, include).Replace('\\', '/');
            return File.Exists(candidate) ? candidate : null;
        }

        // ------------------------------------------------------------------
        // Fix: reuse the existing converter.

        private static bool Convert(string materialPath, List<string> prefabPaths)
        {
            try
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (mat == null) return false;

                bool isParticle = MaterialConverter.IsParticleMaterial(mat);

                var row = new MaterialPlanRow
                {
                    usage = new MaterialUsage
                    {
                        assetPath = materialPath,
                        guid = AssetDatabase.AssetPathToGUID(materialPath),
                        shaderName = mat.shader != null ? mat.shader.name : "(none)",
                        // Must be populated: the executor's post-pass walks these to
                        // refresh ParticleSystemRenderers. An empty list silently skips
                        // the flipbook auto-fix.
                        usingPrefabs = prefabPaths ?? new List<string>(),
                    },
                    kind = isParticle
                        ? MaterialConvertKind.ConvertParticle
                        : MaterialConvertKind.ConvertOpaqueToUniversal,
                    targetShader = isParticle
                        ? DreamParkShaderNames.Particles
                        : DreamParkShaderNames.Universal,
                    approved = true,
                    hardSkip = false,
                    // MUST stay null/empty. MaterialPlanRow.WillBeModified requires
                    // `approved && !hardSkip && string.IsNullOrEmpty(skipReason)`, so a
                    // row carrying an explanatory message here is silently skipped.
                    skipReason = null,
                };

                var result = MaterialConverterExecutor.Apply(
                    new List<MaterialPlanRow> { row },
                    (p, msg) => EditorUtility.DisplayProgressBar("Convert to DreamPark", msg, p));

                // Shader identity changed, so every memoised verdict is now meaningless.
                InvalidateShaderCaches();

                if (result == null || result.converted == 0)
                {
                    Debug.LogWarning($"[DreamPark] {materialPath} was not converted "
                                   + $"(processed {result?.processed ?? 0}, skipped {result?.skipped ?? 0}, "
                                   + $"failed {result?.failed ?? 0}). The converter leaves particle and "
                                   + "unsupported vendor shaders alone by design — see the Material "
                                   + "Converter window for the full plan.");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not convert {materialPath}: {e}");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
#endif
