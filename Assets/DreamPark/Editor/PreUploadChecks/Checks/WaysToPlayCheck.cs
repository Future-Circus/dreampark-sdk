#if UNITY_EDITOR && !DREAMPARKCORE
// Ways to play (contract ways-to-play.md §3/§4): every runner has a header that reads, exactly one default, every edge a
// runner can open has an Open look, levels play fully closed, 2/4-tile levels fit a 1-tile board. The findings come from
// DreamPark.WaysToPlay.RunnerChecks (shared with the Park Simulator's Ways to Play panel).
using System.Collections.Generic;
using UnityEditor;
using DreamPark.WaysToPlay;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class WaysToPlayCheck : IPreUploadCheck
    {
        public const string CheckId = "ways-to-play";
        public string Id => CheckId;
        public string DisplayName => "Ways to play (runner scripts, edge sockets)";
        public CheckSeverity DefaultSeverity => CheckSeverity.Blocking;
        public bool RunsInAdvisoryScan => true;
        public string Rationale =>
            "Each Runners/<id>.lua.txt is a way to play the phone offers. Its DreamPark.runner{ name, blurb, cover, default } "
            + "header is read at upload; exactly one is the default. Levels must play with every Edge Socket closed, and any "
            + "edge a neighbouring tile or a runner can open needs an Open look (Docs/RUNNERS.md).";

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.contentId)) return CheckResult.Skipped(CheckId, "no content selected");
            string root = string.IsNullOrEmpty(ctx.contentRoot) ? "Assets/Content/" + ctx.contentId : ctx.contentRoot;
            var findings = new List<Finding>();
            foreach (RunnerFinding f in RunnerChecks.Run(root))
            {
                if (f.severity == RunnerFindingSeverity.Info) continue;
                string path = f.assetPath;
                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = f.severity == RunnerFindingSeverity.Blocking ? CheckSeverity.Blocking : CheckSeverity.Warning,
                    assetPath = path,
                    assetGuid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                    subKey = f.message.GetHashCode().ToString("x"),
                    title = f.message,
                    detail = "Docs/RUNNERS.md · DreamPark ▸ Park Simulator ▸ Ways to Play… shows the same findings and runs the way to play.",
                };
                if (!string.IsNullOrEmpty(path))
                    finding.fixes.Add(FixAction.Navigate("Open", () =>
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(path);
                        if (asset != null) AssetDatabase.OpenAsset(asset);
                    }));
                findings.Add(finding);
            }
            return CheckResult.From(CheckId, findings);
        }
    }
}
#endif
