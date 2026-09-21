#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class DreamSequenceRequiredCheck : IPreUploadCheck
    {
        public const string CheckId = "dream-sequence-required";
        public string Id => CheckId;
        public string DisplayName => "Dream Sequence fallback";
        public string Rationale => "Every game needs a generated 12 × 18 ft Dream Sequence so it can deploy when a park cannot fit the required attractions.";
        public CheckSeverity DefaultSeverity => CheckSeverity.Blocking;
        public bool RunsInAdvisoryScan => true;

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var dreams = ctx.roots.Where(r => r.kind == ContentRootKindPublic.Attraction)
                .Select(r => new
                {
                    root = r,
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(r.assetPath),
                })
                .Where(x => x.prefab != null && (x.prefab.GetComponent<DreamSequenceTemplate>() != null
                    || x.root.name.IndexOf("DreamSequence", System.StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            if (dreams.Count > 0)
            {
                var findings = new List<Finding>();
                foreach (var dream in dreams)
                {
                    DreamSequenceTemplate sequence = dream.prefab.GetComponent<DreamSequenceTemplate>();
                    if (sequence == null)
                    {
                        findings.Add(InvalidLevel(dream.root.assetPath, "template",
                            "Dream Sequence template is missing",
                            $"'{dream.root.name}' is named like a Dream Sequence but has no DreamSequenceTemplate component."));
                        continue;
                    }

                    for (int i = 0; i < (sequence.levels?.Count ?? 0); i++)
                    {
                        DreamSequenceLevel level = sequence.levels[i];
                        string slot = $"level-{i + 1}";
                        if (level == null || string.IsNullOrEmpty(level.sourceGuid))
                        {
                            findings.Add(InvalidLevel(dream.root.assetPath, slot,
                                "Dream Sequence attraction reference is missing",
                                $"'{dream.root.name}' level {i + 1} no longer identifies its source attraction. Regenerate or update the Dream Sequence."));
                            continue;
                        }

                        string sourcePath = AssetDatabase.GUIDToAssetPath(level.sourceGuid);
                        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                        AttractionTemplate attraction = source != null ? source.GetComponent<AttractionTemplate>() : null;
                        if (attraction == null)
                        {
                            findings.Add(InvalidLevel(dream.root.assetPath, slot + "-missing",
                                "Dream Sequence attraction is missing",
                                $"'{dream.root.name}' references '{level.displayName}', but its source prefab no longer exists or is no longer an Attraction."));
                        }
                        else if (!DreamSequenceCompatibility.IsCompatible(attraction))
                        {
                            findings.Add(InvalidLevel(dream.root.assetPath, slot + "-size",
                                "Dream Sequence attraction no longer fits",
                                $"'{source.name}' in '{dream.root.name}' no longer fits the 12 × 18 ft Dream Sequence room at its authored or baked shrink size. Resize/rebake it or remove it from the sequence."));
                        }
                    }
                }
                return findings.Count == 0 ? CheckResult.Clean(Id) : CheckResult.From(Id, findings);
            }

            return CheckResult.From(Id, new List<Finding>
            {
                new Finding
                {
                    checkId = Id,
                    severity = CheckSeverity.Blocking,
                    assetGuid = AssetDatabase.AssetPathToGUID(ctx.contentRoot),
                    assetPath = ctx.contentRoot,
                    subKey = "missing",
                    title = "Generate a Dream Sequence before upload",
                    detail = "Open Content Uploader → Content Overview → Dreams and choose Add Dream Sequence. The default start, end, and hand-collider navigation make the fallback immediately testable."
                }
            });
        }

        private static Finding InvalidLevel(string dreamPath, string subKey, string title, string detail)
        {
            return new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Blocking,
                assetGuid = AssetDatabase.AssetPathToGUID(dreamPath),
                assetPath = dreamPath,
                subKey = subKey,
                title = title,
                detail = detail,
            };
        }
    }
}
#endif
