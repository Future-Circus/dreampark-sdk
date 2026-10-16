#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using DreamPark.Editor;
using UnityEditor;

namespace DreamPark.PreUploadChecks.Checks
{
    /// <summary>Reports the authored placements that the package compiler will reject.</summary>
    public sealed class PackageReferencesCheck : IPreUploadCheck
    {
        public const string CheckId = "package-references";
        public string Id => CheckId;
        public string DisplayName => "Package references";
        public string Rationale => "Every placed item must resolve to an Attraction or Prop prefab in this content folder.";
        public CheckSeverity DefaultSeverity => CheckSeverity.Blocking;
        public bool RunsInAdvisoryScan => true;

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            Scan(ctx, false, "Adventure", findings);
            Scan(ctx, true, "Sequence", findings);
            return findings.Count == 0 ? CheckResult.Clean(Id) : CheckResult.From(Id, findings);
        }

        private static void Scan(PreUploadCheckContext ctx, bool sequenceMode, string mode,
            List<Finding> findings)
        {
            ContentSequenceStore.Data layout = ContentSequenceStore.Load(ctx.contentId, sequenceMode);
            if (!layout.hasExplicitEndpoints) return;

            if (!sequenceMode)
            {
                if (!string.IsNullOrEmpty(layout.startGuid))
                    AddIfInvalid(ctx, mode, "Start Point", "start", layout.startGuid, findings);
                if (!string.IsNullOrEmpty(layout.endGuid))
                    AddIfInvalid(ctx, mode, "End Point", "end", layout.endGuid, findings);
            }

            foreach (ContentSequenceStore.Entry item in layout.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null || item.hidden) continue;
                if (item.IsWorld)
                {
                    for (int i = 0; i < (item.attractionGuids?.Count ?? 0); i++)
                    {
                        string childId = item.attractionIds != null && i < item.attractionIds.Count
                            ? item.attractionIds[i] : i.ToString();
                        AddIfInvalid(ctx, mode, string.IsNullOrEmpty(item.name) ? "Group" : item.name,
                            "group-" + item.id + "-" + childId, item.attractionGuids[i], findings);
                    }
                }
                else
                    AddIfInvalid(ctx, mode, "Placement", "placement-" + item.id,
                        item.attractionGuid, findings);
            }
        }

        private static void AddIfInvalid(PreUploadCheckContext ctx, string mode,
            string location, string placementKey, string guid, List<Finding> findings)
        {
            if (DreamParkPackageCompiler.IsValidAuthoredReference(ctx.contentId, guid)) return;
            string path = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
            string description = string.IsNullOrEmpty(path)
                ? "The prefab was deleted or its GUID can no longer be resolved."
                : "The referenced asset is not a valid Attraction or Prop prefab in this content folder.";
            findings.Add(new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Blocking,
                assetGuid = AssetDatabase.AssetPathToGUID(ctx.contentRoot),
                assetPath = string.IsNullOrEmpty(path) ? ctx.contentRoot : path,
                subKey = mode + ":" + placementKey,
                title = mode + " " + location + " has an invalid prefab",
                detail = description + " Remove or replace this placement in the " + mode
                    + " organizer before upload. Reference: " + (guid ?? "no GUID"),
            });
        }
    }
}
#endif
