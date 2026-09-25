#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using DreamPark.ConvertReady;
using DreamPark.EditorTools.Shaders;

namespace DreamPark.PreUploadChecks.Checks
{
    // Materials on a DreamPark Shader Graph shader that are set to Surface Type =
    // Opaque with Alpha Clipping off.
    //
    // WHY THIS BLOCKS
    //
    // This is MetaOcclusionCheck's blind spot, and it is a nastier failure than the
    // one that check catches. MetaOcclusionCheck asks "does this shader integrate
    // Meta environment occlusion?" — and for these materials the answer is yes. The
    // shader is correct. The MATERIAL throws the result away.
    //
    // Meta's OcclusionSubGraph drives the occlusion result into the fragment's alpha.
    // A Transparent surface consumes that alpha through the blend, so occlusion works
    // with no further setup. An Opaque surface has nothing downstream that reads
    // alpha — the clip is the only consumer — so with Alpha Clipping off the
    // occlusion value is computed, written, and discarded. The object renders at full
    // opacity over the guest's hands, furniture and walls.
    //
    // So the content passes every other gate, renders perfectly in the Editor and in
    // Play mode, and is wrong only on a headset, only in passthrough, and only after
    // it has shipped. That is worth a modal.
    //
    // WHY IT IS SAFE TO BLOCK ON
    //
    // PreUploadCheck.cs's policy — "a modal with an 'Upload anyway' button is a
    // scarce resource" — is about checks that are wrong often enough to get trained
    // through. This one cannot be wrong. The condition is three property reads on a
    // material whose shader we ship: Surface is Opaque, and clipping is not genuinely
    // on. There is no heuristic, no source scan, no third state. Every finding on an
    // editable material has a one-click fix that resolves it completely, which is the
    // other half of earning a block.
    //
    // WHY THE SCOPE IS WIDER THAN MetaOcclusionCheck'S
    //
    // That check walks prefab contents so it can restrict itself to MeshRenderers,
    // because a LineRenderer or a UI Image genuinely does not want to be occluded
    // against real-world depth. Here the scoping does not matter: a DreamPark
    // Universal/Unlit material that is Opaque-with-no-clip is misconfigured wherever
    // it is used, and enabling the clip is correct in every case. So this reads
    // AssetDatabase.GetDependencies instead — no prefab deserialization, a fraction
    // of the cost, and it also catches materials referenced by something other than a
    // renderer.
    public sealed class OpaqueAlphaClipCheck : IPreUploadCheck
    {
        public const string CheckId = "opaque-alpha-clip";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Opaque materials with Alpha Clipping off"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }

        // Cheaper than MetaOcclusionCheck — no LoadPrefabContents, no shader source
        // scan — but it still deserializes every material a content root references,
        // and the advisory pass fires on every asset save while the uploader is open.
        // Kept out for the same reason its sibling is: the gate is where this needs to
        // be right, and freezing the editor between saves is how a suite gets turned
        // off.
        public bool RunsInAdvisoryScan { get { return false; } }

        public string Rationale
        {
            get
            {
                return "Meta's environment occlusion writes its result into alpha, and an Opaque "
                     + "surface discards alpha unless it is clipped — so these materials have the "
                     + "occlusion wiring but throw the answer away, and render over the guest's "
                     + "real room on a headset. Fixing it is one toggle and cannot break anything "
                     + "that was not already broken.";
            }
        }

        // Same rule as MetaOcclusionCheck: an SDK material is not the creator's to
        // fix, and blocking their upload on one is a red gate with no action behind
        // it. DreamParkMaterialPostprocessor repairs these automatically on import, so
        // by the time anyone sees this check they are already correct.
        private const string SdkPrefix = "Assets/DreamPark/";

        // Model formats whose materials can be EMBEDDED rather than extracted. A
        // material inside one of these is a sub-asset: it is read-only, so it gets
        // reported without a fix action rather than skipped — MetaOcclusionCheck makes
        // the same distinction, and an embedded material is exactly as broken on a
        // headset as an extracted one.
        private static readonly HashSet<string> ModelExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".fbx", ".obj", ".blend", ".dae", ".3ds", ".dxf", ".gltf", ".glb",
            };

        private sealed class Entry
        {
            public Material mat;
            public string ownerPath;        // the .mat, or the model file for embedded
            public string key;              // stable identity, also the finding subKey
            public bool embedded;
            public List<ContentRootInfo> users = new List<ContentRootInfo>();
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

            int i = 0;
            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count) * 0.7f,
                             $"Collecting materials in {root.name}…");
                i++;

                try
                {
                    Collect(root, entries);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not collect materials from {root.assetPath}: {e.Message}");
                }
            }

            // Ordered so the findings list, and the ignore keys derived from it, do not
            // depend on dictionary enumeration order.
            var ordered = entries.Values.OrderBy(e => e.key, StringComparer.Ordinal).ToList();

            int j = 0;
            foreach (var entry in ordered)
            {
                ctx.Progress(0.7f + (float)j / Mathf.Max(1, ordered.Count) * 0.3f,
                             "Checking alpha clipping…");
                j++;

                if (entry.mat == null) continue;
                if (!DreamParkMaterialRules.IsBrokenForOcclusion(entry.mat)) continue;

                // Same ordering discipline as above: whichever root is named first must
                // not depend on how the dictionary happened to enumerate, because
                // assetGuid feeds Finding.IgnoreKey and an unstable key silently stops
                // suppressing an ignored finding.
                var users = entry.users.OrderBy(u => u.assetPath, StringComparer.Ordinal).ToList();
                var owner = users.FirstOrDefault();
                string userList = string.Join(", ", users.Select(u => u.name).Distinct().Take(6));
                string shaderName = entry.mat.shader != null ? entry.mat.shader.name : "(none)";
                string displayName = entry.embedded
                    ? entry.mat.name + " (in " + Path.GetFileName(entry.ownerPath) + ")"
                    : Path.GetFileNameWithoutExtension(entry.ownerPath);

                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = CheckSeverity.Blocking,
                    assetGuid = owner != null ? owner.guid : AssetDatabase.AssetPathToGUID(entry.ownerPath),
                    assetPath = owner != null ? owner.assetPath : entry.ownerPath,
                    subKey = entry.key,
                    title = $"{displayName} — Opaque with Alpha Clipping off",
                    detail =
                        $"{entry.ownerPath}\nShader: {shaderName}\nUsed by: {userList}\n\n"
                      + DreamParkMaterialRules.LockExplanation
                      + "\n\nThe shader is correct — this is a material setting, so the Meta Occlusion "
                      + "check passes and the object still renders over the guest's real room."
                      + (entry.embedded
                            ? "\n\nThis material is embedded in a model file and is read-only, so it "
                            + "cannot be fixed in place. DreamPark can extract the model's materials "
                            + "beside it, rebind the model, and enable clipping on the affected "
                            + "extracted materials."
                            : ""),
                };

                string capturedPath = entry.ownerPath;
                bool contentOwned = ContentRootScanner.IsUnderContentRoot(capturedPath, ctx.contentRoot);
                if (!contentOwned)
                {
                    finding.detail += "\n\nThis asset is outside the selected content package. DreamPark "
                                    + "will not rewrite a shared or package-owned source in place; resolve "
                                    + "it into the content folder first, then run the material repair.";
                }

                if (!contentOwned && owner != null
                    && OutsideContentFolderCheck.Classify(capturedPath, ctx.contentId)
                        == OutsideContentFolderCheck.Verdict.Violation
                    && OutsideContentFolderCheck.CanOfferMove(capturedPath))
                {
                    string rootPath = owner.assetPath;
                    string contentId = ctx.contentId;
                    string contentRoot = ctx.contentRoot;
                    finding.fixes.Add(FixAction.Interactive("Resolve into content first…", completed =>
                    {
                        OutsideContentDependencyResolverPopup.Show(
                            rootPath, capturedPath, contentId, contentRoot, completed);
                    }, OutsideContentFolderCheck.CheckId, MetaOcclusionCheck.CheckId));
                }
                else if (contentOwned && entry.embedded)
                {
                    finding.fixes.Add(new FixAction(
                        "Extract Materials + Enable Alpha Clipping",
                        () => ExtractAndFix(capturedPath))
                    {
                        tooltip = "Extracts all embedded materials beside the model, rebinds the model, "
                                + "then enables Alpha Clipping on affected DreamPark materials.",
                        bulkKey = "extract-and-enable-alpha-clipping",
                        bulkLabel = "Extract Materials + Enable Alpha Clipping",
                        // One extraction repairs every embedded material in this
                        // model; sibling findings would otherwise run it twice.
                        canBulk = false,
                        affectedPaths = new[] { capturedPath },
                        alsoRerunCheckIds = new[]
                        {
                            MetaOcclusionCheck.CheckId,
                            OutsideContentFolderCheck.CheckId,
                        },
                        confirmTitle = "Extract materials and enable Alpha Clipping",
                        confirmMessage =
                            $"Extract all embedded materials from {Path.GetFileName(capturedPath)} and "
                          + "enable Alpha Clipping where required?\n\n"
                          + "The .mat files will be created beside the model and the model importer will "
                          + "be rebound to them. Affected Opaque materials will move from Geometry to "
                          + "AlphaTest; fully opaque textures will look unchanged.\n\n"
                          + "WATCH FOR: asset packs sometimes contain empty or garbage albedo alpha. If "
                          + "an object disappears, its texture alpha needs repair.\n\n"
                          + "This modifies model import remaps and creates new assets. It cannot be "
                          + "undone via Ctrl-Z; use version control to revert.",
                    });
                }
                else if (contentOwned)
                {
                    finding.fixes.Add(new FixAction(
                        "Enable Alpha Clipping",
                        () => Fix(capturedPath))
                    {
                        tooltip = "Sets Alpha Clipping on and resyncs the render queue and _ALPHATEST_ON "
                                + "keyword. Nothing else about the material changes.",
                        confirmTitle = "Enable Alpha Clipping",
                        confirmMessage =
                            $"Enable Alpha Clipping on {Path.GetFileName(capturedPath)}?\n\n"
                          + "This is what the material should already have been set to — it is required "
                          + "for Meta environment occlusion on an Opaque surface.\n\n"
                          + "Visible change: pixels whose alpha falls below the threshold (default 0.5) "
                          + "stop being drawn, and the material moves from the Geometry queue to "
                          + "AlphaTest. For an albedo texture with a fully opaque alpha channel — the "
                          + "normal case — nothing changes at all.\n\n"
                          + "WATCH FOR: if the albedo's alpha channel is empty or garbage (some asset "
                          + "packs ship one), the object will visibly disappear. That is not this fix "
                          + "misbehaving — it means the material could never have been occluded, and the "
                          + "texture's alpha needs fixing. Check it in the Scene view afterwards.\n\n"
                          + "Cannot be undone via Ctrl-Z. Use version control to revert.",
                    });
                }

                // Navigation last: the actionable fix should be the leftmost button,
                // and DrawSectionBulkAction keys its batch button off fixes[0].
                var pingTarget = entry.mat;
                finding.fixes.Add(FixAction.Navigate("Select material", () =>
                {
                    if (pingTarget != null) { Selection.activeObject = pingTarget; EditorGUIUtility.PingObject(pingTarget); }
                }));

                findings.Add(finding);
            }

            return CheckResult.From(CheckId, findings);
        }

        private static void Collect(ContentRootInfo root, Dictionary<string, Entry> entries)
        {
            foreach (string dep in AssetDatabase.GetDependencies(root.assetPath, true))
            {
                if (string.IsNullOrEmpty(dep)) continue;
                if (dep.StartsWith(SdkPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (ContentRootScanner.IsThirdPartyLocal(dep)) continue;

                string ext = Path.GetExtension(dep);

                if (string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(dep);
                    if (mat == null) continue;
                    if (!DreamParkMaterialRules.IsGoverned(mat)) continue;
                    Add(entries, dep, dep, mat, false, root);
                }
                else if (ModelExtensions.Contains(ext))
                {
                    // Embedded materials are sub-assets of the model, so they never
                    // appear in the dependency list under their own path. Filtering on
                    // ".mat" alone misses them entirely, and asset packs ship them by
                    // the hundred.
                    foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(dep))
                    {
                        var mat = obj as Material;
                        if (mat == null) continue;
                        if (!DreamParkMaterialRules.IsGoverned(mat)) continue;
                        string key = dep + "::" + mat.name;
                        string guid;
                        long localId;
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                                mat, out guid, out localId))
                            key = dep + "::" + localId;
                        Add(entries, key, dep, mat, true, root);
                    }
                }
            }
        }

        private static void Add(
            Dictionary<string, Entry> entries,
            string key, string ownerPath, Material mat, bool embedded, ContentRootInfo root)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e))
            {
                entries[key] = e = new Entry
                {
                    mat = mat, ownerPath = ownerPath, key = key, embedded = embedded,
                };
            }
            if (!e.users.Contains(root)) e.users.Add(root);
        }

        private static bool Fix(string matPath)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                Debug.LogWarning($"[DreamPark] Could not load '{matPath}' to enable alpha clipping.");
                return false;
            }

            // Enforce returns false when there was nothing to change, which for a
            // finding that was just reported means someone fixed it in another window
            // between the scan and the click. Resolution is decided by re-reading the
            // material, not by what Enforce returned.
            DreamParkMaterialRules.EnforceAndDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);

            // IsSatisfied returns true for materials we do not govern, so a material
            // whose shader was swapped away between scan and click would otherwise
            // report "resolved" having had nothing done to it.
            return DreamParkMaterialRules.IsGoverned(mat)
                && DreamParkMaterialRules.IsSatisfied(mat);
        }

        private static bool ExtractAndFix(string modelPath)
        {
            try
            {
                var extraction = new ConversionResult { sourcePath = modelPath, ok = true };
                int extracted;
                List<string> extractedPaths;
                string destination = AssetClassifier.DefaultMaterialsFolder(modelPath);

                if (!AssetClassifier.ExtractEmbeddedMaterials(
                        modelPath, destination, extraction, out extracted, out extractedPaths))
                {
                    Debug.LogWarning($"[DreamPark] Could not extract embedded materials from "
                                   + $"{modelPath}: {extraction.error ?? "unknown extraction error"}");
                    return false;
                }

                bool fixedAll = true;
                foreach (string path in extractedPaths)
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null) { fixedAll = false; continue; }
                    if (!DreamParkMaterialRules.IsBrokenForOcclusion(material)) continue;
                    if (!Fix(path)) fixedAll = false;
                }

                AssetDatabase.SaveAssets();

                bool blockingExtractedRemains = extractedPaths
                    .Select(AssetDatabase.LoadAssetAtPath<Material>)
                    .Where(m => m != null)
                    .Any(DreamParkMaterialRules.IsBrokenForOcclusion);

                bool blockingEmbeddedRemains = AssetDatabase.LoadAllAssetsAtPath(modelPath)
                    .OfType<Material>()
                    .Any(m => AssetDatabase.IsSubAsset(m)
                           && DreamParkMaterialRules.IsBrokenForOcclusion(m));

                return fixedAll && !blockingExtractedRemains && !blockingEmbeddedRemains;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not extract and repair materials from {modelPath}: {e}");
                return false;
            }
        }
    }
}
#endif
