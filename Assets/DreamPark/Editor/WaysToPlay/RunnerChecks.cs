// ─────────────────────────────────────────────────────────────────────
//  RunnerChecks.cs — pre-upload checks for ways to play (contract §3/§4).
//  Pure over RunnerCatalog / TitleLevels; the SDK's PreUploadCheck wrapper
//  (WaysToPlayCheck) and the Ways to Play panel both show these findings.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DreamPark.WaysToPlay
{
    public enum RunnerFindingSeverity { Info = 0, Warning = 1, Blocking = 2 }

    public sealed class RunnerFinding
    {
        public RunnerFindingSeverity severity;
        public string message;
        public string assetPath;
        public override string ToString() => $"[{severity}] {message}{(string.IsNullOrEmpty(assetPath) ? "" : "  (" + assetPath + ")")}";
    }

    public static class RunnerChecks
    {
        // dp.edges.open(tile, "left", "gate")  /  dp.edges.lock(tile, 'back', true)
        static readonly Regex EdgeCall = new Regex(
            @"dp\s*\.\s*edges\s*\.\s*(open|lock)\s*\(\s*[^,()]+,\s*(?:""([a-z]+)""|'([a-z]+)'|[^,()]+)\s*(?:,\s*(?:""([A-Za-z0-9_-]+)""|'([A-Za-z0-9_-]+)'|[^,()]+))?",
            RegexOptions.Compiled);

        public static List<RunnerFinding> Run(string contentRoot)
        {
            var f = new List<RunnerFinding>();
            List<RunnerEntry> runners = RunnerCatalog.Discover(contentRoot);
            foreach (string stray in RunnerCatalog.StrayFiles(contentRoot))
                f.Add(W(RunnerFindingSeverity.Warning, "this runner is a bare .lua file and will not ship; rename it to .lua.txt", stray));
            if (runners.Count == 0)
            {
                f.Add(W(RunnerFindingSeverity.Info, "no ways to play (Runners/*.lua.txt): the title plays its levels in order, one per tile", RunnerCatalog.RunnersFolder(contentRoot)));
                return f;
            }

            // Headers parse; ids unique; exactly one default.
            foreach (var r in runners)
            {
                if (!r.header.Ok) f.Add(W(RunnerFindingSeverity.Blocking, $"way to play '{r.runnerId}': {r.header.error}", r.assetPath));
                foreach (string w in r.header.warnings) f.Add(W(RunnerFindingSeverity.Warning, $"way to play '{r.runnerId}': {w}", r.assetPath));
                if (r.coverMissing) f.Add(W(RunnerFindingSeverity.Warning, $"way to play '{r.runnerId}': cover '{r.header.cover}' not found (the phone shows the title card instead)", r.assetPath));
                else if (!string.IsNullOrEmpty(r.coverAssetPath) && string.IsNullOrEmpty(r.coverMime))
                    f.Add(W(RunnerFindingSeverity.Warning, $"way to play '{r.runnerId}': cover must be a .png or .jpg", r.coverAssetPath));
                if (string.IsNullOrEmpty(r.runnerId)) f.Add(W(RunnerFindingSeverity.Blocking, "a runner file name has no letters or digits", r.assetPath));
            }
            if (runners.Count > RunnerCatalog.MaxRunners) f.Add(W(RunnerFindingSeverity.Blocking, $"{runners.Count} ways to play; a title can ship at most {RunnerCatalog.MaxRunners}", RunnerCatalog.RunnersFolder(contentRoot)));
            foreach (var g in runners.GroupBy(r => r.runnerId).Where(g => g.Count() > 1))
                f.Add(W(RunnerFindingSeverity.Blocking, $"{g.Count()} runner files become the same id '{g.Key}': rename one", g.First().assetPath));
            int marked = runners.Count(r => HeaderMarksDefault(r.source));
            if (marked > 1) f.Add(W(RunnerFindingSeverity.Blocking, $"{marked} ways to play say default = true; exactly one may", RunnerCatalog.RunnersFolder(contentRoot)));
            else if (marked == 0 && runners.Count > 1)
                f.Add(W(RunnerFindingSeverity.Info, $"no way to play says default = true; '{runners[0].runnerId}' (first by name) is the default", runners[0].assetPath));

            // Levels: closed looks, open looks for every edge a runner can open, 1-tile frames.
            List<TitleLevel> levels = TitleLevels.Discover(contentRoot);
            var requested = new HashSet<string>();        // "side|look" from dp.edges.open
            foreach (var r in runners)
                foreach (Match m in EdgeCall.Matches(StripComments(r.source)))
                {
                    if (m.Groups[1].Value != "open") continue;
                    string side = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : "*";
                    string look = m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Success ? m.Groups[5].Value : "";
                    requested.Add(side + "|" + look);
                }
            foreach (var lvl in levels)
            {
                foreach (var s in lvl.sockets)
                {
                    if (lvl.prefab != null && s.FacingSide(lvl.prefab.transform) != s.side)
                        f.Add(W(RunnerFindingSeverity.Warning, $"level '{lvl.name}': the {s.SideName} Edge Socket faces {EdgeSides.Name(s.FacingSide(lvl.prefab.transform))} (its +Z must point out of the level; front = −Z, toward the portal)", lvl.assetPath));
                    if (s.closedLook == null)
                        f.Add(W(RunnerFindingSeverity.Warning, $"level '{lvl.name}' {s.SideName} edge has no Closed look: standing alone that side shows a gap", lvl.assetPath));
                    if (!s.HasOpenLook)
                        f.Add(W(RunnerFindingSeverity.Blocking, $"level '{lvl.name}' {s.SideName} edge has no Open look, but a neighbouring tile can join it there", lvl.assetPath));
                    foreach (string req in requested)
                    {
                        string[] p = req.Split('|');
                        if (p[0] != "*" && p[0] != s.SideName) continue;
                        bool plain = p[1].Length == 0 || p[1] == "open" || p[1] == "doorway";   // = the default open look
                        if (!plain && !s.HasNamedLook(p[1]))
                            f.Add(W(RunnerFindingSeverity.Blocking, $"a runner opens {(p[0] == "*" ? "an" : "the " + p[0])} edge as \"{p[1]}\" but level '{lvl.name}' {s.SideName} has no open look named \"{p[1]}\"", lvl.assetPath));
                    }
                }
                if (lvl.footprintTiles == 1 && lvl.sockets.Count == 0 && levels.Count > 1)
                    f.Add(W(RunnerFindingSeverity.Info, $"level '{lvl.name}' has no Edge Sockets: next to another tile its own edge shows (no doorway)", lvl.assetPath));
                if (lvl.footprintTiles > 1 && !lvl.fitsOneTileByFrame)
                    f.Add(W(RunnerFindingSeverity.Warning, $"level '{lvl.name}' is a {lvl.footprintTiles}-tile level ({lvl.sizeFeet.x:0}×{lvl.sizeFeet.y:0} ft) with no layout frame that fits a 1-tile board (12×18 ft)", lvl.assetPath));
            }

            // Every level a runner names exists.
            var names = new HashSet<string>(levels.Select(l => l.name), System.StringComparer.OrdinalIgnoreCase);
            if (names.Count > 0)
                foreach (var r in runners)
                    foreach (string lvl in LevelNamesIn(r.source))
                        if (!names.Contains(lvl))
                            f.Add(W(RunnerFindingSeverity.Warning, $"way to play '{r.runnerId}' loads \"{lvl}\", which is not a level in this title", r.assetPath));

            // Vote options players would see as raw ids (GridRunner beta v1: "pitrun · crusher · gemrush" on the card).
            foreach (var r in runners)
                foreach (List<string> ids in BareVoteIds(r.source))
                    f.Add(W(RunnerFindingSeverity.Warning, $"way to play '{r.runnerId}': dp.flow.choose option{(ids.Count == 1 ? "" : "s")} {string.Join(", ", ids.Select(i => "\"" + i + "\""))} " +
                        $"{(ids.Count == 1 ? "has" : "have")} no label, so players see the id on the vote card and the phone. Write options = {{ {{id = \"{ids[0]}\", label = \"…\"}}, … }} (or add labels = {{…}}); onChoice still gets the id", r.assetPath));
            return f;
        }

        // dp.flow.choose{ … options = { "a", 'b' } … }: the literal string options of each call that gives no labels and
        // keeps the platform panel. Options from a variable are not seen here; the director warns about those at run time.
        static readonly Regex ChooseCall = new Regex(@"dp\s*\.\s*flow\s*\.\s*choose\s*[({]", RegexOptions.Compiled);
        static readonly Regex OptionsList = new Regex(@"\boptions\s*=\s*\{(?<list>[^{}]*)\}", RegexOptions.Compiled);
        static readonly Regex StringLit = new Regex(@"""([^""]*)""|'([^']*)'", RegexOptions.Compiled);
        static readonly Regex NoPanel = new Regex(@"\b(labels\s*=|panel\s*=\s*false)", RegexOptions.Compiled);
        public static IEnumerable<List<string>> BareVoteIds(string src)
        {
            string s = StripComments(src ?? "");
            foreach (Match m in ChooseCall.Matches(s))
            {
                // the call's own text: from its opening bracket to the matching close
                int start = m.Index + m.Length - 1, depth = 0, end = s.Length;
                for (int i = start; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '(' || c == '{') depth++;
                    else if ((c == ')' || c == '}') && --depth == 0) { end = i + 1; break; }
                }
                string call = s.Substring(start, end - start);
                if (NoPanel.IsMatch(call)) continue;
                Match ol = OptionsList.Match(call);
                if (!ol.Success) continue;
                var ids = new List<string>();
                foreach (Match lit in StringLit.Matches(ol.Groups["list"].Value))
                {
                    string v = lit.Groups[1].Success ? lit.Groups[1].Value : lit.Groups[2].Value;
                    if (RunnerDirector.LooksLikeId(v) && !ids.Contains(v)) ids.Add(v);
                }
                if (ids.Count > 0) yield return ids;
            }
        }

        static readonly Regex DefaultTrue = new Regex(@"default\s*=\s*true", RegexOptions.Compiled);
        static readonly Regex HeaderBlock = new Regex(@"DreamPark\s*\.\s*runner\s*(\(\s*)?\{(?<body>[^}]*)\}", RegexOptions.Compiled | RegexOptions.Singleline);
        static bool HeaderMarksDefault(string src)
        {
            Match m = HeaderBlock.Match(StripComments(src ?? ""));
            return m.Success && DefaultTrue.IsMatch(m.Groups["body"].Value);
        }

        // Literal level names in dp.flow.load / swapWhenClear / transform calls.
        static readonly Regex LoadCall = new Regex(
            @"dp\s*\.\s*flow\s*\.\s*(?:load|transform)\s*\(\s*(?:""([^""]+)""|'([^']+)')|dp\s*\.\s*flow\s*\.\s*swapWhenClear\s*\(\s*[^,()]+,\s*(?:""([^""]+)""|'([^']+)')",
            RegexOptions.Compiled);
        public static IEnumerable<string> LevelNamesIn(string src)
        {
            foreach (Match m in LoadCall.Matches(StripComments(src ?? "")))
                for (int g = 1; g <= 4; g++) if (m.Groups[g].Success) { yield return m.Groups[g].Value; break; }
        }

        static readonly Regex LongComment = new Regex(@"--\[(=*)\[.*?\]\1\]", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex LineComment = new Regex(@"--[^\n]*", RegexOptions.Compiled);
        public static string StripComments(string s) => LineComment.Replace(LongComment.Replace(s ?? "", ""), "");

        static RunnerFinding W(RunnerFindingSeverity s, string m, string p) => new RunnerFinding { severity = s, message = m, assetPath = p };
    }
}
