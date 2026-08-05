#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // Two content root prefabs with the same file name.
    //
    // WHY THIS BLOCKS
    //
    // Nothing in the SDK detected this, and one layer silently worked around it.
    // A collision breaks four things at once:
    //
    //  1. The Addressables address. ContentProcessor builds
    //     "{gameId}/Levels/{size}/{name}" (or ".../Props/{category}/{name}") and
    //     assigns it per GUID with a plain `entry.address = desiredAddress`. Two
    //     distinct entries end up carrying the same address string. Unity does not
    //     error; Addressables.LoadAssetAsync resolves it nondeterministically from
    //     the catalog's m_InternalIds order. No validation, no warning, no log.
    //
    //  2. The preview PNG. Written to Previews/{name}.png, so the second prefab
    //     clobbers the first — and the "skip if the file already exists" rule means
    //     whichever renders first wins PERMANENTLY until someone force-regenerates.
    //     This collides even when the addresses don't (different size / category),
    //     which is why this check keys on the bare NAME rather than the address.
    //
    //  3. PreviewMetadataStore overrides, which are keyed by bare prefab name.
    //     Camera framing authored for one prefab silently applies to the other.
    //
    //  4. GameArea.resourceName / PropTemplate.resourceName, stamped with the
    //     address. ContentProcessor's own comment: "This is what lets the headset
    //     attribute revenue to the individual attraction." Two attractions sharing an
    //     address share a revenue key. That is the severe one.
    //
    // Prior art that this is real: SmartBundleGrouper.BuildRootGroupNames already
    // disambiguates colliding names — for GROUP names only. Someone hit this at the
    // bundle layer and patched that layer. Addresses, previews and resourceName were
    // left unprotected.
    //
    // Scoped to the selected content folder: addresses are namespaced by gameId, so a
    // name shared across two different content packages is fine.
    public sealed class DuplicateNamesCheck : IPreUploadCheck
    {
        public const string CheckId = "duplicate-names";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Duplicate prefab names"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "The level loader and level previews resolve content by name. Two prefabs "
                     + "sharing one name collide on their Addressables address, their preview PNG, "
                     + "and the resourceName used for revenue attribution — silently, and "
                     + "nondeterministically.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();

            // Ordinal, not OrdinalIgnoreCase: Path.GetFileNameWithoutExtension feeds
            // the address verbatim, so "A_Boss" and "A_boss" really do produce
            // different addresses. Case-only clashes are still reported, one severity
            // lower — they cannot coexist in one folder on a case-insensitive
            // filesystem, and their preview PNGs collide regardless.
            var exact = ctx.roots
                .GroupBy(r => r.name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            var exactNames = new HashSet<string>(exact.Select(g => g.Key), StringComparer.Ordinal);

            // Case-only groups must EXCLUDE members already reported as exact
            // collisions. Without that, {A_Boss, A_Boss, A_boss} produced a Blocking
            // finding and a Warning finding for the same two prefabs, with the same
            // checkId + guid + subKey — i.e. the same ignore key. Ignoring the harmless
            // Warning (which needs no typed reason) then also suppressed the Blocking
            // one, and two prefabs shipped sharing an address and a revenue key.
            var caseOnly = ctx.roots
                .Where(r => !exactNames.Contains(r.name))
                .GroupBy(r => r.name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            // Shared across ALL groups so two different groups can never suggest the
            // same replacement name.
            var takenNames = new HashSet<string>(
                ctx.roots.Select(r => r.name), StringComparer.OrdinalIgnoreCase);

            foreach (var group in exact)
                AddGroup(findings, group.ToList(), takenNames, CheckSeverity.Blocking, false);

            foreach (var group in caseOnly)
                AddGroup(findings, group.ToList(), takenNames, CheckSeverity.Warning, true);

            return CheckResult.From(CheckId, findings);
        }

        private void AddGroup(List<Finding> findings, List<ContentRootInfo> group,
                              HashSet<string> takenNames, CheckSeverity severity, bool caseOnly)
        {
            // Deterministic: sort by asset path, keep the first, rename the rest.
            // Renaming all-but-one minimises churn. (SmartBundleGrouper disambiguates
            // ALL members of a colliding set instead, because group identity has to
            // round-trip build over build. Asset names have no such constraint.)
            group = group.OrderBy(r => r.assetPath, StringComparer.Ordinal).ToList();

            for (int i = 0; i < group.Count; i++)
            {
                var root = group[i];
                bool keeper = i == 0 && !caseOnly;

                string others = string.Join(", ",
                    group.Where(g => g != root).Select(g => g.assetPath));

                string detail = caseOnly
                    ? $"'{root.name}' differs from another prefab only by letter case ({others}). "
                    + "On macOS and Windows these cannot live in the same folder, and their preview "
                    + "PNGs overwrite each other."
                    : $"'{root.name}' is also used by {others}. "
                    + $"Both resolve to the same preview PNG (Previews/{root.name}.png) and the same "
                    + "PreviewMetadataStore key; if they are also the same size/category they share an "
                    + "Addressables address and a resourceName, so revenue attribution and level "
                    + "loading both become nondeterministic.";

                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = severity,
                    assetGuid = root.guid,
                    assetPath = root.assetPath,
                    // The mode is part of the key so an exact-collision finding and a
                    // case-only finding for the same prefab are independently
                    // ignorable.
                    subKey = root.name + (caseOnly ? "|case" : "|exact"),
                    title = keeper
                        ? $"{root.KindLabel} '{root.name}' — name shared with {group.Count - 1} other prefab(s)"
                        : $"{root.KindLabel} '{root.name}' — duplicate name",
                    detail = detail,
                };

                if (!keeper)
                {
                    string suggested = SuggestName(root.name, takenNames);
                    takenNames.Add(suggested);

                    var captured = root;
                    var capturedName = suggested;
                    finding.fixes.Add(new FixAction(
                        $"Rename to {suggested}",
                        // The name is re-derived at CLICK time, not reused from scan
                        // time: another rename in the same batch may have taken it.
                        () => Rename(captured, capturedName))
                    {
                        tooltip = "Renames the prefab, its preview image and its preview override entry, "
                                + "then re-stamps addresses via ContentProcessor.",
                        confirmTitle = "Rename prefab",
                        confirmMessage = BuildRenameWarning(captured, capturedName),
                    });
                }

                string openPath = root.assetPath;
                finding.fixes.Add(FixAction.Navigate("Open", () =>
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(openPath);
                    if (asset != null) AssetDatabase.OpenAsset(asset);
                }));

                findings.Add(finding);
            }
        }

        private static string BuildRenameWarning(ContentRootInfo root, string newName)
        {
            return $"Rename '{root.name}' → '{newName}'?\n\n"
                 + $"{root.assetPath}\n\n"
                 + "This also renames the preview image and moves the preview override entry, then "
                 + "re-runs ContentProcessor to re-stamp Addressables addresses and resourceName.\n\n"
                 + "Two things worth knowing before a release:\n"
                 + "• The backend catalog resourceName is the asset-path stem, so it changes. Old "
                 + "catalog rows orphan and previews/dimensions upload as skipped until the next "
                 + "build republishes the catalog.\n"
                 + "• The Smart bundle group name changes, so this content re-uploads in full "
                 + "instead of patching.\n\n"
                 + "Cannot be undone with Ctrl-Z. Use version control to revert.";
        }

        private static string SuggestName(string baseName, HashSet<string> taken)
        {
            for (int n = 2; n < 1000; n++)
            {
                string candidate = $"{baseName}_{n}";
                if (!taken.Contains(candidate)) return candidate;
            }
            return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // Renaming a content root is not just AssetDatabase.RenameAsset. There is
        // exactly one RenameAsset call site in the whole SDK and it renames a folder,
        // so none of this is automated anywhere else.
        private static bool Rename(ContentRootInfo root, string preferredName)
        {
            string oldPath = root.assetPath;
            string oldName = root.name;

            string contentId = ContentIdFromPath(oldPath);
            if (string.IsNullOrEmpty(contentId))
            {
                Debug.LogWarning($"[DreamPark] Could not determine the content folder for {oldPath}.");
                return false;
            }

            try
            {
                // Re-derive at click time. A batch rename may have taken the name that
                // was free when the finding was built, and AssetDatabase.RenameAsset
                // would then silently produce "Foo_2 1.prefab".
                var taken = new HashSet<string>(
                    ContentRootScanner.Scan(contentId).Select(r => r.name),
                    StringComparer.OrdinalIgnoreCase);
                string newName = taken.Contains(preferredName)
                    ? SuggestName(oldName, taken)
                    : preferredName;

                // Preview settings must be read BEFORE the rename, while the old key
                // still resolves.
                PreviewSettings carriedSettings;
                bool hadSettings = PreviewMetadataStore.TryGet(contentId, oldName, out carriedSettings);

                // AssetDatabase.RenameAsset takes the new LEAF name only — no path, no
                // extension — and returns an error STRING (empty means success).
                string err = AssetDatabase.RenameAsset(oldPath, newName);
                if (!string.IsNullOrEmpty(err))
                {
                    Debug.LogWarning($"[DreamPark] Could not rename {oldPath}: {err}");
                    return false;
                }

                RenamePreviewImages(contentId, oldName, newName);

                if (hadSettings)
                {
                    PreviewMetadataStore.Clear(contentId, oldName);
                    PreviewMetadataStore.Set(contentId, newName, carriedSettings);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // ContentProcessor.ForceUpdateContent re-stamps every prefab, address
                // and label in the package — deliberately NOT called per rename. A
                // five-prefab batch would run five full passes. It is run once, after
                // the batch, by RequestRestamp below.
                RequestRestamp(contentId);

                Debug.Log($"[DreamPark] Renamed '{oldName}' → '{newName}'.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Rename of {oldPath} failed: {e}");
                return false;
            }
        }

        private static string ContentIdFromPath(string assetPath)
        {
            const string prefix = "Assets/Content/";
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (!assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            string rest = assetPath.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : null;
        }

        // Coalesces the expensive re-stamp to one pass per batch. delayCall fires once
        // after the current tick, so N renames in a row schedule N callbacks but only
        // the first one with work left to do actually runs the pass.
        private static string pendingRestampContentId;

        private static void RequestRestamp(string contentId)
        {
            if (pendingRestampContentId != null) { pendingRestampContentId = contentId; return; }
            pendingRestampContentId = contentId;

            EditorApplication.delayCall += () =>
            {
                string id = pendingRestampContentId;
                pendingRestampContentId = null;
                if (string.IsNullOrEmpty(id)) return;

                try { ContentProcessor.ForceUpdateContent(id); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not re-stamp {id} after renaming: {e.Message}");
                }
            };
        }

        private static void RenamePreviewImages(string contentId, string oldName, string newName)
        {
            string previews = $"{ContentRootScanner.ContentFolder}/{contentId}/Previews";
            if (!AssetDatabase.IsValidFolder(previews)) return;

            // The bare-name form is what every code path in the SDK actually reads or
            // writes. The "{name}_preview.png" sibling form is documented in CLAUDE.md
            // but has zero code support — renamed here anyway so a hand-made file
            // doesn't get orphaned, but nothing depends on it.
            string[] exts = { ".png", ".jpg", ".jpeg" };
            string[] stems = { oldName, oldName + "_preview" };
            string[] newStems = { newName, newName + "_preview" };

            for (int s = 0; s < stems.Length; s++)
            {
                foreach (var ext in exts)
                {
                    string path = $"{previews}/{stems[s]}{ext}";
                    if (!File.Exists(path)) continue;

                    string err = AssetDatabase.RenameAsset(path, newStems[s]);
                    if (!string.IsNullOrEmpty(err))
                        Debug.LogWarning($"[DreamPark] Could not rename preview {path}: {err}");
                }
            }
        }
    }
}
#endif
