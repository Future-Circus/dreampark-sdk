#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// The Playlist Studio without the window, for CI and for a developer's coding agent (Docs/PLAYLIST-STUDIO.md
    /// § Agents). Every command prints ONE machine-readable JSON document.
    ///
    /// Batchmode (editor closed):
    ///   Unity -batchmode -quit -projectPath . -executeMethod DreamPark.Editor.PlaylistStudio.PlaylistStudioHeadless.Run
    ///         -playlists check|generate|convert -contentId MyGame [-count 10] [-remix &lt;playlistId&gt; -variant boss-rush]
    ///         [-accept] [-out report.json]
    ///   Exit code: 0 = no errors (check) / done; 1 = errors or a failed call; 2 = bad arguments.
    /// A running editor (Tools/agent/u.sh run_script --file Tools/Builders/PlaylistsCli.cs --entry PlaylistsCli.Check): see that builder.
    /// </summary>
    public static class PlaylistStudioHeadless
    {
        public static string ReportDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "PlaylistStudio"));

        /// The titles of this project (Assets/Content/*, minus the SDK's reserved samples).
        public static List<string> Titles()
            => AssetDatabase.GetSubFolders("Assets/Content").Select(Path.GetFileName)
                .Where(id => !ContentFolders.IsReserved(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ── check: validator + chapters/unlocks + challenges + fit matrix ──

        public static JSONObject Check(string contentId, out bool ok)
        {
            PlaylistStudioSession s = PlaylistStudioSession.FromProject(contentId);
            var report = JSONObject.emptyObject;
            report.AddField("command", "check");
            report.AddField("titleId", contentId);
            report.AddField("file", ContentPlaylistStore.AssetPath(contentId));
            report.AddField("exists", ContentPlaylistStore.Exists(contentId));
            ok = s.loadError == null;
            if (s.loadError != null) { report.AddField("ok", false); report.AddField("error", s.loadError); return report; }
            List<PlaylistIssue> issues = s.Validate();
            var fileIssues = JSONObject.emptyArray;
            foreach (PlaylistIssue i in issues.Where(i => i.playlistId == null)) fileIssues.Add(Issue(i));
            report.AddField("fileIssues", fileIssues);
            var list = JSONObject.emptyArray;
            int fitsSmall = 0, valid = 0;
            foreach (PackagePlaylist p in s.data.playlists)
            {
                var mine = issues.Where(i => i.playlistId == p.id).ToList();
                var rows = s.Fit(p);
                bool passes = PlaylistFitCheck.Passes(rows, out string why);
                bool small = PlaylistFitCheck.FitsSmallRoom(rows);
                bool isValid = !mine.Any(i => i.isError) && passes;
                if (isValid) valid++;
                if (small) fitsSmall++;
                var o = JSONObject.emptyObject;
                o.AddField("id", p.id ?? ""); o.AddField("name", p.name ?? ""); o.AddField("kind", p.kind ?? "");
                o.AddField("minutes", p.arc.Sum(l => l.minutes)); o.AddField("legs", p.arc.Count);
                o.AddField("arc", PlaylistArcModel.OneLine(p));
                o.AddField("valid", isValid);
                o.AddField("errors", PlaylistStudioJson.StringArray(mine.Where(i => i.isError).Select(i => i.message)));
                o.AddField("warnings", PlaylistStudioJson.StringArray(mine.Where(i => !i.isError).Select(i => i.message)));
                var fit = JSONObject.emptyObject;
                foreach (PlaylistFitRow r in rows) fit.AddField(r.space.id, r.verdict);
                o.AddField("fit", fit);
                if (!passes) o.AddField("fitProblem", why);
                o.AddField("fitsSmallRoom", small);
                o.AddField("gate", PlaylistGates.Describe(p));
                o.AddField("challenges", PlaylistStudioJson.StringArray(p.arc.SelectMany(l => l.stops).Select(st =>
                    string.IsNullOrEmpty(st.challenge) ? PlaylistChallenges.Derive(st.role, l_of(p, st)?.objective?.type, s.Item(st.attraction)?.defaultChallenge) : st.challenge)));
                list.Add(o);
            }
            report.AddField("playlists", list);
            var summary = JSONObject.emptyObject;
            summary.AddField("playlists", s.data.playlists.Count);
            summary.AddField("valid", valid);
            summary.AddField("fitSmallRoom", fitsSmall);
            summary.AddField("starters", s.data.playlists.Count(PlaylistGates.IsStarter));
            summary.AddField("chained", s.data.playlists.Count(p => p.continueWith.Count > 0 || !PlaylistGates.IsStarter(p)));
            summary.AddField("attractions", s.library.Count);
            summary.AddField("attractionsInPlaylists", s.library.Count(i => s.data.playlists.Any(p => p.arc.Any(l => l.stops.Any(st => st.attraction == i.leaf || st.alternates.Contains(i.leaf))))));
            summary.AddField("challengeCatalog", s.hasCatalog);
            report.AddField("summary", summary);
            ok = !issues.Any(i => i.isError) && valid == s.data.playlists.Count && s.data.playlists.Count > 0;
            report.AddField("ok", ok);
            return report;
        }

        static PlaylistLeg l_of(PackagePlaylist p, PlaylistStop st) => p.arc.FirstOrDefault(l => l.stops.Contains(st));

        static JSONObject Issue(PlaylistIssue i)
        {
            var o = JSONObject.emptyObject;
            o.AddField("error", i.isError);
            o.AddField("message", i.message);
            return o;
        }

        // ── generate / remix / convert ──

        public static JSONObject ProposalsReport(PlaylistStudioSession s, string command, IEnumerable<PlaylistProposal> made, bool accepted)
        {
            var report = JSONObject.emptyObject;
            report.AddField("command", command);
            report.AddField("titleId", s.titleId);
            var list = JSONObject.emptyArray;
            foreach (PlaylistProposal p in made)
            {
                var o = JSONObject.emptyObject;
                o.AddField("canSave", p.CanSave);
                o.AddField("state", p.state);
                o.AddField("fitsSmallRoom", p.FitsSmallRoom);
                o.AddField("arc", PlaylistArcModel.OneLine(p.playlist));
                o.AddField("errors", PlaylistStudioJson.StringArray(p.issues.Where(i => i.isError).Select(i => i.message)));
                o.AddField("warnings", PlaylistStudioJson.StringArray(p.issues.Where(i => !i.isError).Select(i => i.message)));
                o.AddField("repairs", PlaylistStudioJson.StringArray(p.repairs));
                if (p.fitProblem != null) o.AddField("fitProblem", p.fitProblem);
                var fit = JSONObject.emptyObject;
                foreach (PlaylistFitRow r in p.fit) fit.AddField(r.space.id, r.verdict);
                o.AddField("fit", fit);
                o.AddField("playlist", PlaylistStudioJson.WritePlaylist(p.playlist, s.titleId));
                list.Add(o);
            }
            report.AddField("proposals", list);
            report.AddField("messages", PlaylistStudioJson.StringArray(s.messages));
            report.AddField("accepted", accepted);
            report.AddField("ok", true);
            return report;
        }

        public static JSONObject Generate(string contentId, int count, string remixId, string variant, bool accept, out bool ok)
        {
            PlaylistStudioSession s = PlaylistStudioSession.FromProject(contentId);
            PackagePlaylist baseP = string.IsNullOrEmpty(remixId) ? null : s.data.playlists.FirstOrDefault(p => p.id == remixId);
            if (!string.IsNullOrEmpty(remixId) && baseP == null) { ok = false; return Fail("remix", $"no playlist '{remixId}' in {contentId}"); }
            GeneratorResponse r = PlaylistStudioApi.SendBlocking(contentId, PlaylistStudioApi.Body(s, count, baseP, variant));
            Directory.CreateDirectory(ReportDir);
            if (!r.success) { ok = false; return Fail(baseP != null ? "remix" : "generate", (r.code ?? "") + " " + r.error); }
            var made = s.TakeResponse(r, baseP != null ? "remix" : "generate");
            ok = true;
            if (accept) { foreach (PlaylistProposal p in made.Where(p => p.CanSave)) s.Accept(p); s.Save(); }
            return ProposalsReport(s, baseP != null ? "remix" : "generate", made, accept);
        }

        public static JSONObject Convert(string contentId, bool accept)
        {
            PlaylistStudioSession s = PlaylistStudioSession.FromProject(contentId);
            var made = s.ConvertOrganizers();
            if (accept) { foreach (PlaylistProposal p in made.Where(p => p.CanSave)) s.Accept(p); if (s.dirty) s.Save(); }
            return ProposalsReport(s, "convert", made, accept);
        }

        static JSONObject Fail(string command, string error)
        {
            var o = JSONObject.emptyObject;
            o.AddField("command", command);
            o.AddField("ok", false);
            o.AddField("error", error.Trim());
            return o;
        }

        // ── batchmode entry ──

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            bool Flag(string name) => Array.IndexOf(args, name) >= 0;
            string command = Arg("-playlists") ?? "check";
            string contentId = Arg("-contentId") ?? Titles().FirstOrDefault();
            string outPath = Arg("-out");
            int code;
            JSONObject report;
            if (string.IsNullOrEmpty(contentId)) { report = Fail(command, "no -contentId and no title under Assets/Content"); code = 2; }
            else
            {
                bool ok;
                switch (command)
                {
                    case "check": report = Check(contentId, out ok); code = ok ? 0 : 1; break;
                    case "generate":
                        int.TryParse(Arg("-count") ?? "10", out int count);
                        report = Generate(contentId, Mathf.Clamp(count, 1, 15), Arg("-remix"), Arg("-variant"), Flag("-accept"), out ok);
                        code = ok ? 0 : 1; break;
                    case "convert": report = Convert(contentId, Flag("-accept")); code = 0; break;
                    default: report = Fail(command, "use -playlists check|generate|convert"); code = 2; break;
                }
            }
            string json = PlaylistStudioJson.Pretty(report);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, json + "\n");
            Console.WriteLine("PLAYLISTS_REPORT_BEGIN\n" + json + "\nPLAYLISTS_REPORT_END");
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
#endif
