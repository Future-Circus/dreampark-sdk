// ─────────────────────────────────────────────────────────────────────
//  EngineClassStripCheck.cs — an engine component class this title's content uses that
//  dreampark-core's player builds may strip (sal-daisy, Oct 23 2026).
//
//  WHY THIS EXISTS
//
//  Core builds with engine code stripping. Content loads from remote bundles, so a
//  Unity component class that only content uses is dropped from the player unless core's
//  Assets/link.xml preserves it, and every such component then silently fails to load on
//  a headset or phone ("Could not produce class with ID …"). Oct 2026: SuperAdventureLand's
//  daisy stems and palm trunks (ConfigurableJoint) flew apart into the sky on device while
//  the editor looked perfect.
//
//  WHAT COUNTS
//
//  Every object in this title's shipping YAML assets (prefabs, materials, controllers, masks …;
//  not scenes) whose class has a runtime UnityEngine type and is missing from
//  ContentEngineClasses.json, the list core preserves (synced with the SDK). ThirdPartyLocal
//  never ships and is skipped.
//
//  WHY WARNING
//
//  The fix is in core (rerun Tools/agent/content_engine_classes.py, ship a player build), not
//  in this title, and the class may be preserved already by core code the list doesn't know.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class EngineClassStripCheck : IPreUploadCheck
    {
        public const string CheckId = "engine-class-strip";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Engine classes core may strip"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }
        // A text pass over the title's YAML headers; no asset loads.
        public bool RunsInAdvisoryScan { get { return false; } }

        public string Rationale
        {
            get
            {
                return "DreamPark's player strips engine classes its own code doesn't use. A Unity component that only "
                     + "your content uses (a ConfigurableJoint, a force field, an avatar mask …) is missing on "
                     + "headsets and phones unless core preserves it, and the component silently never loads there. "
                     + "Ask core to add it (Tools/agent/content_engine_classes.py) before this ships.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.contentId))
                return CheckResult.Skipped(CheckId, "no content selected");
            string root = string.IsNullOrEmpty(ctx.contentRoot) ? "Assets/Content/" + ctx.contentId : ctx.contentRoot;
            var manifest = ContentEngineClasses.Load(out _);
            if (manifest == null) return CheckResult.Skipped(CheckId, ContentEngineClasses.ManifestPath + " missing (sdk-sync)");

            var findings = new List<Finding>();
            foreach (var (name, type, files) in ContentEngineClasses.Unpreserved(root, manifest,
                         p => p.IndexOf("/ThirdPartyLocal/", StringComparison.Ordinal) < 0))
            {
                string first = files[0];
                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = CheckSeverity.Warning,
                    assetGuid = AssetDatabase.AssetPathToGUID(first),
                    assetPath = first,
                    subKey = "class:" + name,
                    title = name + " — used by " + files.Count + (files.Count == 1 ? " asset" : " assets")
                            + ", not in core's preserved engine classes",
                    detail = type.FullName + " (" + type.Assembly.GetName().Name + ") is in "
                           + string.Join(", ", files.Take(5).Select(f => System.IO.Path.GetFileName(f)))
                           + (files.Count > 5 ? ", …" : "") + ". Core's player may strip it, and then these components "
                           + "never load on device. Ask core to rerun Tools/agent/content_engine_classes.py (it adds "
                           + "the class to Assets/link.xml) and ship a new player build.",
                };
                finding.fixes.Add(FixAction.Navigate("Select asset", () =>
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(first);
                    if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
                }));
                findings.Add(finding);
            }
            return CheckResult.From(CheckId, findings);
        }
    }
}
#endif
