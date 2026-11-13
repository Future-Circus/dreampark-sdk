#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // WE NEVER SCALE OUR ASSETS TO FIT (Aidan, Oct 19 2026): "We NEVER SCALE DOWN OUR ASSETS, we only compact/remove/add
    // to fit the space better" — scaling is "an artistic decision, not a decision for smaller layouts". The platform's
    // shrink / grow only moves content; a layout FRAME that rescales an object (a "Compact" frame shrinking Snow Dunes)
    // breaks the same rule from the content side, so it blocks the upload. Fix: make the frame compact (move, switch off,
    // switch on) instead, or change the asset's scale in Base if that is the artistic intent.
    public sealed class FrameScaleOverridesCheck : IPreUploadCheck
    {
        public const string CheckId = "frame-scale-overrides";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Layout frames that rescale content"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "Layouts fit a space by compacting, removing or adding pieces, never by scaling them. "
                     + "A layout frame that changes an object's scale makes it look tiny (or huge) in smaller rooms.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            int i = 0;
            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count), $"Checking layout frames of {root.name}…");
                i++;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root.assetPath);
                AttractionTemplate attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (attraction == null) continue;
                foreach (string s in AttractionPackingBaker.GetFrameScaleOverrides(attraction))
                    findings.Add(new Finding
                    {
                        checkId = CheckId,
                        severity = CheckSeverity.Blocking,
                        assetGuid = root.guid,
                        assetPath = root.assetPath,
                        subKey = s,
                        title = $"{root.name}: a layout frame rescales content",
                        detail = s + ". Layouts never scale assets to fit; compact (move), switch off or switch on instead.",
                    });
            }
            return CheckResult.From(CheckId, findings);
        }
    }
}
#endif
