// ─────────────────────────────────────────────────────────────────────
//  LiteralTitleIdCheck.cs — the title's own content id written as a Lua
//  string literal ("CrashCourse", 'LaserTag', "Zombiez/FX_Boom").
//
//  WHY THIS EXISTS
//
//  A Beta upload builds the same source under a second id ("<id>Beta"): every
//  GameArea / PlayerRig gameId, every Addressables address and every LuaBehaviour
//  string injection holding the id is stamped to the Beta id for that build. A
//  literal in Lua SOURCE is not, so on a Beta headset the script compares "<id>"
//  against stamped "<id>Beta" ids, reads and writes the release title's storage,
//  or asks for "<id>/…" addresses the Beta catalog does not contain. Oct 2026:
//  LaserTag's roster stayed empty and its hub/trophy progress split, CrashCourse's
//  races never started — all from literals that worked fine in the release.
//
//  The fix is to resolve the id at runtime, the Zombiez order:
//      storage.gameId → dp.game_id(go) → dp_title_of(go) → PlayerRig.Instance.gameId
//
//  WHAT COUNTS
//
//  A string literal whose value is exactly the content id, or starts with
//  "<id>/". Comments are skipped, and so is CS["<id>"] (a C# namespace lookup,
//  not a value) — dotted names such as "<id>.SomeType" never match in the first
//  place. A line can opt out with the marker comment  -- dp:allow-literal-id
//  (e.g. a migration that reads the release title's old keys on purpose).
//
//  WHY WARNING
//
//  Same policy as the rest of the suite: some literals are deliberate, and a
//  release upload is unaffected. It is worth reading before a Beta goes out,
//  not worth a gate nobody can argue with.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class LiteralTitleIdCheck : IPreUploadCheck
    {
        public const string CheckId = "literal-title-id";
        public const string AllowMarker = "dp:allow-literal-id";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Title id written as a Lua literal"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // A text pass over this title's .lua.txt files; no asset loads.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "Lua that names this title's id as a string (\"<id>\" or \"<id>/…\") keeps that id in a "
                     + "Beta upload, where everything else is stamped \"<id>Beta\": the script then compares "
                     + "against the wrong id, uses the release title's storage, or loads addresses the Beta "
                     + "catalog doesn't have. Resolve the id at runtime instead (storage.gameId → dp.game_id → "
                     + "dp_title_of → PlayerRig.Instance.gameId).";
            }
        }

        public sealed class Hit
        {
            public int line;        // 1-based
            public string value;
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.contentId))
                return CheckResult.Skipped(CheckId, "no content selected");
            string root = string.IsNullOrEmpty(ctx.contentRoot) ? "Assets/Content/" + ctx.contentId : ctx.contentRoot;
            if (!Directory.Exists(root)) return CheckResult.Clean(CheckId);

            var findings = new List<Finding>();
            foreach (string file in Directory.GetFiles(root, "*.lua.txt", SearchOption.AllDirectories)
                         .Select(p => p.Replace('\\', '/'))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                // Same scope as LuaSurfaceScanner: ThirdPartyLocal never ships, and
                // Editor/ or @editor-only scripts never run at a venue.
                if (HasSegment(file, "ThirdPartyLocal") || HasSegment(file, "Editor")) continue;
                string src;
                try { src = File.ReadAllText(file); } catch { continue; }
                if (src.IndexOf(LuaSurfaceScanner.EditorOnlyMarker, StringComparison.Ordinal) >= 0) continue;

                List<Hit> hits = FindLiteralIds(src, ctx.contentId);
                if (hits.Count == 0) continue;
                findings.Add(MakeFinding(file, ctx.contentId, hits));
            }
            return CheckResult.From(CheckId, findings);
        }

        private static Finding MakeFinding(string file, string contentId, List<Hit> hits)
        {
            string name = Path.GetFileName(file);
            var lines = string.Join(", ", hits.Take(12).Select(h => h.line + " (\"" + h.value + "\")"))
                        + (hits.Count > 12 ? ", …" : "");
            var finding = new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Warning,
                assetGuid = AssetDatabase.AssetPathToGUID(file),
                assetPath = file,
                // Keyed on the count: ignoring today's literals must not also hide one added later.
                subKey = "literal:" + hits.Count,
                title = name + " — title id '" + contentId + "' written as a literal ("
                        + hits.Count + (hits.Count == 1 ? " place" : " places") + ")",
                detail = "Line " + lines + ". A Beta upload stamps every id to '" + contentId + "Beta' except "
                       + "literals in Lua source, so this script would compare against, store under or load "
                       + "from the release title's id on a Beta headset. Resolve the id at runtime, Zombiez "
                       + "order: storage.gameId → dp.game_id(self.gameObject) → dp_title_of(self.gameObject) → "
                       + "CS.DreamPark.PlayerRig.Instance.gameId (see LaserTag/Zombiez game-container.lua.txt). "
                       + "If a literal is deliberate (e.g. reading the release title's old keys), end that line "
                       + "with  -- " + AllowMarker,
            };
            int firstLine = hits[0].line;
            finding.fixes.Add(FixAction.Navigate("Open script", () =>
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(file);
                if (asset != null) AssetDatabase.OpenAsset(asset, firstLine);
            }));
            return finding;
        }

        private static bool HasSegment(string path, string segment)
        {
            foreach (string part in path.Split('/'))
                if (string.Equals(part, segment, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// String literals in Lua source whose value is exactly <paramref name="contentId"/> or
        /// starts with "<paramref name="contentId"/>/". Skips comments (line and long), CS["…"]
        /// namespace lookups and lines carrying the opt-out marker. Pure; unit-tested.
        /// </summary>
        public static List<Hit> FindLiteralIds(string src, string contentId)
        {
            var hits = new List<Hit>();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(contentId)) return hits;

            string[] rawLines = src.Split('\n');
            int n = src.Length, i = 0, line = 1;
            while (i < n)
            {
                char c = src[i];
                if (c == '\n') { line++; i++; continue; }

                // Comments: --[[ … ]] / --[==[ … ]==] or -- to end of line.
                if (c == '-' && i + 1 < n && src[i + 1] == '-')
                {
                    int level;
                    if (i + 2 < n && src[i + 2] == '[' && LongBracketLevel(src, i + 2, out level))
                    {
                        i = SkipLongBracket(src, i + 2, level, ref line);
                        continue;
                    }
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }

                // Long strings [[ … ]] / [=[ … ]=].
                if (c == '[')
                {
                    int level;
                    if (LongBracketLevel(src, i, out level))
                    {
                        int startLine = line;
                        int open = i;
                        int end = SkipLongBracket(src, i, level, ref line);
                        int bodyStart = open + level + 2;
                        int bodyEnd = Math.Max(bodyStart, end - level - 2);
                        string body = src.Substring(bodyStart, Math.Min(bodyEnd, n) - bodyStart);
                        if (body.StartsWith("\n", StringComparison.Ordinal)) body = body.Substring(1);
                        Consider(body, startLine, open, src, rawLines, contentId, hits);
                        i = end;
                        continue;
                    }
                    i++;
                    continue;
                }

                // Short strings '…' / "…".
                if (c == '"' || c == '\'')
                {
                    int open = i;
                    var sb = new StringBuilder();
                    i++;
                    while (i < n && src[i] != c && src[i] != '\n')
                    {
                        if (src[i] == '\\' && i + 1 < n)
                        {
                            sb.Append(src[i]).Append(src[i + 1]);
                            if (src[i + 1] == '\n') line++;
                            i += 2;
                            continue;
                        }
                        sb.Append(src[i]);
                        i++;
                    }
                    Consider(sb.ToString(), line, open, src, rawLines, contentId, hits);
                    if (i < n && src[i] == c) i++;
                    continue;
                }
                i++;
            }
            return hits;
        }

        private static void Consider(string value, int line, int openIndex, string src, string[] rawLines,
            string contentId, List<Hit> hits)
        {
            bool matches = string.Equals(value, contentId, StringComparison.Ordinal)
                           || value.StartsWith(contentId + "/", StringComparison.Ordinal);
            if (!matches) return;
            if (line - 1 < rawLines.Length && rawLines[line - 1].IndexOf(AllowMarker, StringComparison.Ordinal) >= 0) return;
            if (IsCsIndex(src, openIndex)) return;
            hits.Add(new Hit { line = line, value = value });
        }

        // CS["<id>"] / CS[ '<id>' ]: a C# namespace lookup, not a title id value.
        private static bool IsCsIndex(string src, int openIndex)
        {
            int j = openIndex - 1;
            while (j >= 0 && (src[j] == ' ' || src[j] == '\t')) j--;
            if (j < 0 || src[j] != '[') return false;
            j--;
            while (j >= 0 && (src[j] == ' ' || src[j] == '\t')) j--;
            if (j < 1 || src[j] != 'S' || src[j - 1] != 'C') return false;
            return j - 2 < 0 || !(char.IsLetterOrDigit(src[j - 2]) || src[j - 2] == '_');
        }

        // True when src[at] opens a long bracket: '[' followed by zero or more '=' and '['.
        private static bool LongBracketLevel(string src, int at, out int level)
        {
            level = 0;
            if (at >= src.Length || src[at] != '[') return false;
            int j = at + 1;
            while (j < src.Length && src[j] == '=') { level++; j++; }
            return j < src.Length && src[j] == '[';
        }

        // Returns the index just past the matching close bracket (or the end of src).
        private static int SkipLongBracket(string src, int at, int level, ref int line)
        {
            string close = "]" + new string('=', level) + "]";
            int start = at + level + 2;
            int end = src.IndexOf(close, start, StringComparison.Ordinal);
            int stop = end < 0 ? src.Length : end + close.Length;
            for (int k = at; k < stop && k < src.Length; k++) if (src[k] == '\n') line++;
            return stop;
        }
    }
}
#endif
