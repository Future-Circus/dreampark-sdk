#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Editor;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    /// <summary>Validates the shipped recipe and source parts, not a generated prefab.</summary>
    public sealed class DreamSequenceRequiredCheck : IPreUploadCheck
    {
        public const string CheckId = "dream-sequence-required";
        public string Id => CheckId;
        public string DisplayName => "Sequence package";
        public string Rationale => "Every title needs a valid single-room Sequence recipe and editable Start, Overlay, Game Over, and Game Manager prefabs.";
        public CheckSeverity DefaultSeverity => CheckSeverity.Blocking;
        public bool RunsInAdvisoryScan => true;

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            string contentId = ctx.contentId;
            string definitionPath = DreamSequencePackageCompiler.DefinitionPath(contentId);
            DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                DreamSequencePackageDefinition>(definitionPath);
            if (definition == null)
            {
                Finding missing = FindingFor(ctx.contentRoot, "missing", "Sequence package is not compiled",
                    "The Sequence is authored in the organizer, but its addressable package definition has not been built. No Dream Sequence prefab is needed.");
                missing.fixes.Add(new FixAction("Build Sequence Package", () =>
                {
                    try { return DreamSequencePackageCompiler.Compile(contentId) != null; }
                    catch (Exception e) { Debug.LogException(e); return false; }
                })
                {
                    tooltip = "Builds the lightweight runtime recipe from Start, Overlay, Game Over, Game Manager, and the saved order.",
                    canBulk = false,
                    affectedPaths = new[] { ctx.contentRoot },
                });
                return CheckResult.From(Id, new List<Finding> { missing });
            }

            var findings = new List<Finding>();
            if (definition.startLevelPrefab == null || definition.overlayLevelPrefab == null
                || definition.gameOverLevelPrefab == null || definition.gameManagerPrefab == null)
                findings.Add(FindingFor(definitionPath, "parts", "Sequence package parts are missing",
                    "Start, Overlay, Game Over, and Game Manager must each be an editable prefab. Rebuild the package definition."));
            else
            {
                foreach (GameObject part in new[] { definition.startLevelPrefab,
                    definition.overlayLevelPrefab, definition.gameOverLevelPrefab })
                    if (part.GetComponent<AttractionTemplate>()?.HasPackingBake != true)
                        findings.Add(FindingFor(AssetDatabase.GetAssetPath(part), "packing",
                            "Sequence special level has no packing bake",
                            $"'{part.name}' must be baked so the shared room can shrink and conform to the floor."));
            }

            ContentSequenceStore.Data layout = ContentSequenceStore.Load(contentId, true);
            var validGuids = new List<string>();
            foreach (string guid in ContentSequenceStore.Flatten(layout))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AttractionTemplate attraction = prefab?.GetComponent<AttractionTemplate>();
                if (prefab?.GetComponent<PropTemplate>() != null) continue; // visibly excluded in Sequence UI
                if (attraction == null || attraction is DreamSequenceTemplate)
                {
                    findings.Add(FindingFor(string.IsNullOrEmpty(path) ? ctx.contentRoot : path,
                        "missing-" + guid, "Sequence attraction is missing",
                        "A placed attraction no longer resolves to an Attraction prefab. Remove or replace it in the Sequence organizer."));
                    continue;
                }
                if (!DreamSequenceCompatibility.IsCompatible(attraction))
                {
                    // Optional incompatible items remain visible (dimmed) in
                    // the organizer but are deliberately not compiled. A
                    // required item cannot be omitted from a playable release.
                    if (attraction.gameRequiresAttraction)
                        findings.Add(FindingFor(path, "size-" + guid,
                            "Sequence attraction no longer fits",
                            $"Required attraction '{prefab.name}' cannot fit the 12 × 18 ft room at its authored or baked shrink size. Resize and rebake it, or remove the requirement."));
                    continue;
                }
                validGuids.Add(guid);
            }

            if (validGuids.Count == 0)
                findings.Add(FindingFor(definitionPath, "no-stages",
                    "Sequence has no compatible attractions",
                    "Add at least one attraction that fits the 12 × 18 ft Sequence room. Optional incompatible assets are shown in the organizer but cannot form a playable package or be published to the Web catalog."));

            var compiledGuids = (definition.levels ?? new List<DreamSequenceLevel>())
                .Select(level => level?.sourceGuid ?? "").ToList();
            if (!compiledGuids.SequenceEqual(validGuids, StringComparer.Ordinal)
                || (definition.levels ?? new List<DreamSequenceLevel>()).Any(level =>
                    level == null || string.IsNullOrEmpty(level.address)))
                findings.Add(FindingFor(definitionPath, "order",
                    "Sequence package definition is out of date",
                    "Its streamed levels do not match the current organizer order. Rebuild the package definition."));

            if (findings.Count == 0) return CheckResult.Clean(Id);
            foreach (Finding finding in findings)
                finding.fixes.Add(new FixAction("Rebuild Sequence Package", () =>
                {
                    try { return DreamSequencePackageCompiler.Compile(contentId) != null; }
                    catch (Exception e) { Debug.LogException(e); return false; }
                }) { canBulk = false, affectedPaths = new[] { definitionPath } });
            return CheckResult.From(Id, findings);
        }

        private static Finding FindingFor(string path, string subKey, string title, string detail)
            => new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Blocking,
                assetGuid = AssetDatabase.AssetPathToGUID(path),
                assetPath = path,
                subKey = subKey,
                title = title,
                detail = detail,
            };
    }
}
#endif
