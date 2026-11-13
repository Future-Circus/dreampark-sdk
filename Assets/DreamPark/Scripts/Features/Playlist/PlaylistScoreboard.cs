// ─────────────────────────────────────────────────────────────────────
//  PlaylistScoreboard.cs — the per-player session scoreboard of a playlist
//  RUN (Docs/PLAYLISTS.md § 7.8), carried across legs, titles and queue items
//  so the Results Room can rank players.
//
//  One DETACHED SharedState per runId ("dp.playlist.board/{runId}", session
//  bus, no title, no package): it outlives every leg's package owner. Each
//  player's row "p.{playerId}" = {name, legs:{"<legIndex>": n}}; a report is
//  a request the leader max-merges per (player, leg), so repeats and two
//  devices reporting the same player never double count. Totals follow
//  rules.scoring: best = the best leg, otherwise the sum of legs.
//  Rows are pure (PlaylistScoreRules) so they are tested offline.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    public sealed class PlaylistScoreRow
    {
        public const string KindPlayer = "player", KindCpu = "cpu";
        public string id, name;
        /// player | cpu (CPU racers / opponents ride the same board: "final scores for all players INCLUDING CPUs").
        public string kind = KindPlayer;
        /// A CPU's stable avatar (a title asset key / URL); players use their profile.
        public string avatar;
        /// Optional livery colour "#RRGGBB" (a CPU racer's car, a player's ring) — the Results draw the entrant in it.
        public string color;
        /// A CPU's face from the title's declared cast (CpuRoster, Docs/CPU_PLAYERS.md): the small tag ("CPU" when empty;
        /// DreamSports "DreamPal"), a subtitle ("Tennis Champion"), the difficulty word and the champion flag.
        public string tag, title, difficulty;
        public bool champion;
        /// Per leg: the medal a medal-scored title gave (3 gold · 2 silver · 1 bronze · 0 none); players and CPUs alike.
        public readonly SortedDictionary<int, int> medals = new SortedDictionary<int, int>();
        public readonly SortedDictionary<int, double> legs = new SortedDictionary<int, double>();
        /// Per leg: the finishing place (1 = first), 0 = DNF. Only placings-style results set it.
        public readonly SortedDictionary<int, int> places = new SortedDictionary<int, int>();

        public double Total(string scoring)
        {
            double sum = 0, best = 0;
            foreach (var v in legs.Values) { sum += v; best = Math.Max(best, v); }
            return scoring == PlaylistRunRules.ScoringBest ? best : sum;
        }

        /// Tie-break key for placings: how many 1st, 2nd, 3rd … places (more is better), DNFs last.
        public int CountPlace(int place)
        {
            int n = 0;
            foreach (var v in places.Values) if (v == place) n++;
            return n;
        }

        public int BestFinish()
        {
            int best = int.MaxValue;
            foreach (var v in places.Values) if (v > 0 && v < best) best = v;
            return best;
        }

        public string ToJson()
        {
            var sb = new StringBuilder("{\"name\":\"").Append(PlaylistRunRules.Esc(name ?? "")).Append('"');
            if (kind == KindCpu) sb.Append(",\"k\":\"cpu\"");
            if (!string.IsNullOrEmpty(avatar)) sb.Append(",\"av\":\"").Append(PlaylistRunRules.Esc(avatar)).Append('"');
            if (!string.IsNullOrEmpty(color)) sb.Append(",\"co\":\"").Append(PlaylistRunRules.Esc(color)).Append('"');
            if (!string.IsNullOrEmpty(tag)) sb.Append(",\"tg\":\"").Append(PlaylistRunRules.Esc(tag)).Append('"');
            if (!string.IsNullOrEmpty(title)) sb.Append(",\"tt\":\"").Append(PlaylistRunRules.Esc(title)).Append('"');
            if (!string.IsNullOrEmpty(difficulty)) sb.Append(",\"df\":\"").Append(PlaylistRunRules.Esc(difficulty)).Append('"');
            if (champion) sb.Append(",\"ch\":true");
            sb.Append(",\"legs\":{");
            bool first = true;
            foreach (var kv in legs)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(PlaylistRunRules.Num(kv.Value));
            }
            sb.Append('}');
            if (places.Count > 0)
            {
                sb.Append(",\"pl\":{");
                first = true;
                foreach (var kv in places)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('}');
            }
            if (medals.Count > 0)
            {
                sb.Append(",\"md\":{");
                first = true;
                foreach (var kv in medals)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('}');
            }
            return sb.Append('}').ToString();
        }

        public static PlaylistScoreRow FromJson(string id, JSONObject j)
        {
            var r = new PlaylistScoreRow { id = id };
            if (j == null || j.type != JSONObject.Type.Object) return r;
            r.name = PlaylistRunPlan.Str(j, "name", "");
            r.kind = PlaylistRunPlan.Str(j, "k", KindPlayer) == KindCpu ? KindCpu : KindPlayer;
            r.avatar = PlaylistRunPlan.Str(j, "av", null);
            r.color = PlaylistRunPlan.Str(j, "co", null);
            r.tag = PlaylistRunPlan.Str(j, "tg", null);
            r.title = PlaylistRunPlan.Str(j, "tt", null);
            r.difficulty = PlaylistRunPlan.Str(j, "df", null);
            var ch = j.GetField("ch");
            r.champion = ch != null && ch.type == JSONObject.Type.Bool && ch.boolValue;
            var md = j.GetField("md");
            if (md != null && md.keys != null)
                for (int i = 0; i < md.keys.Count; i++)
                    if (int.TryParse(md.keys[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ml)
                        && md.list[i] != null && md.list[i].type == JSONObject.Type.Number)
                        r.medals[ml] = (int)md.list[i].doubleValue;
            var pl = j.GetField("pl");
            if (pl != null && pl.keys != null)
                for (int i = 0; i < pl.keys.Count; i++)
                    if (int.TryParse(pl.keys[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lg)
                        && pl.list[i] != null && pl.list[i].type == JSONObject.Type.Number)
                        r.places[lg] = (int)pl.list[i].doubleValue;
            var legs = j.GetField("legs");
            if (legs != null && legs.keys != null)
                for (int i = 0; i < legs.keys.Count; i++)
                    if (int.TryParse(legs.keys[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int leg)
                        && legs.list[i] != null && legs.list[i].type == JSONObject.Type.Number)
                        r.legs[leg] = legs.list[i].doubleValue;
            return r;
        }
    }

    public static class PlaylistScoreRules
    {
        /// Points per place when a title reports only places (`placings` scoring): 15·12·10·8·6·4·2·1, then 0.
        public static readonly int[] PlacingPoints = { 15, 12, 10, 8, 6, 4, 2, 1 };
        public static double PointsForPlace(int place) => place >= 1 && place <= PlacingPoints.Length ? PlacingPoints[place - 1] : 0;

        /// One entrant's result in a leg (`dp.playlist.standings` / `report_result{racer, place, points}`): the place
        /// (0 = DNF, which scores 0 and ranks last; NoPlace = a result without a finishing order, e.g. a medal only),
        /// points (else the medal's: gold 3 · silver 2 · bronze 1; else the table's for that place), kind and avatar.
        public static bool ApplyPlacing(PlaylistScoreRow row, int leg, int place, double? points, string name, string kind, string avatar, int medal = -1)
        {
            if (row == null) return false;
            bool changed = false;
            if (!string.IsNullOrEmpty(name) && name != row.name) { row.name = name; changed = true; }
            string k = kind == PlaylistScoreRow.KindCpu ? PlaylistScoreRow.KindCpu : PlaylistScoreRow.KindPlayer;
            if (k != row.kind) { row.kind = k; changed = true; }
            if (!string.IsNullOrEmpty(avatar) && avatar != row.avatar) { row.avatar = avatar; changed = true; }
            int p = place == NoPlace ? NoPlace : Math.Max(0, place);
            if (p != NoPlace && (!row.places.TryGetValue(leg, out int old) || old != p)) { row.places[leg] = p; changed = true; }
            if (medal >= 0 && (!row.medals.TryGetValue(leg, out int oldM) || oldM != medal)) { row.medals[leg] = medal; changed = true; }
            double pts = points ?? (medal >= 0 ? medal : PointsForPlace(p));
            if (p == 0) pts = 0;
            if (!row.legs.TryGetValue(leg, out double oldPts) || Math.Abs(oldPts - pts) > 1e-9) { row.legs[leg] = pts; changed = true; }
            return changed;
        }

        /// `place` when a result has no finishing order (a medal or points only): no place is recorded.
        public const int NoPlace = -1;

        /// The entrant's face (a CPU's declared cast entry, carried on each report so the leader's row has it). Only
        /// non-empty values overwrite; kind "cpu" is sticky (a row never turns back into a player). True = changed.
        public static bool ApplyFace(PlaylistScoreRow row, string kind, string avatar, string color, string tag, string title, string difficulty, bool champion)
        {
            if (row == null) return false;
            bool changed = false;
            if (kind == PlaylistScoreRow.KindCpu && row.kind != PlaylistScoreRow.KindCpu) { row.kind = PlaylistScoreRow.KindCpu; changed = true; }
            if (!string.IsNullOrEmpty(avatar) && avatar != row.avatar) { row.avatar = avatar; changed = true; }
            if (!string.IsNullOrEmpty(color) && color != row.color) { row.color = color; changed = true; }
            if (!string.IsNullOrEmpty(tag) && tag != row.tag) { row.tag = tag; changed = true; }
            if (!string.IsNullOrEmpty(title) && title != row.title) { row.title = title; changed = true; }
            if (!string.IsNullOrEmpty(difficulty) && difficulty != row.difficulty) { row.difficulty = difficulty; changed = true; }
            if (champion && !row.champion) { row.champion = true; changed = true; }
            return changed;
        }

        /// placings order: points, then more 1st places, more 2nd places …, then the best single finish.
        static int ComparePlacings(PlaylistScoreRow a, PlaylistScoreRow b)
        {
            int c = b.Total(PlaylistRunRules.ScoringPlacings).CompareTo(a.Total(PlaylistRunRules.ScoringPlacings));
            if (c != 0) return c;
            for (int place = 1; place <= 12; place++)
            {
                c = b.CountPlace(place).CompareTo(a.CountPlace(place));
                if (c != 0) return c;
            }
            return a.BestFinish().CompareTo(b.BestFinish());
        }

        /// Merge one report (absolute n for that player in that leg). True = changed.
        public static bool Apply(PlaylistScoreRow row, int leg, double n, string name)
        {
            if (row == null) return false;
            bool changed = false;
            if (!string.IsNullOrEmpty(name) && name != row.name) { row.name = name; changed = true; }
            if (!row.legs.TryGetValue(leg, out double old) || n > old) { row.legs[leg] = Math.Max(0, n); changed = true; }
            return changed;
        }

        /// THE ranking (one function for PLAYLIST_STATE, the phone and the Results board): highest first; placings
        /// tie-break by places; then name, then id.
        public static int Compare(PlaylistScoreRow a, PlaylistScoreRow b, string scoring)
        {
            int c = scoring == PlaylistRunRules.ScoringPlacings ? ComparePlacings(a, b) : b.Total(scoring).CompareTo(a.Total(scoring));
            if (c != 0) return c;
            c = string.CompareOrdinal(a.name ?? "", b.name ?? "");
            return c != 0 ? c : string.CompareOrdinal(a.id ?? "", b.id ?? "");
        }

        public static List<PlaylistScoreRow> Ranked(IEnumerable<PlaylistScoreRow> rows, string scoring)
        {
            var list = new List<PlaylistScoreRow>(rows ?? new List<PlaylistScoreRow>());
            list.Sort((a, b) => Compare(a, b, scoring));
            return list;
        }

        /// Ranked, highest first (ties: name, then id), as the PLAYLIST_STATE `scoreboard` array. <paramref name="portraitOf"/>
        /// (id → this device's image URL, CpuRoster.PortraitUrl) adds a CPU's device-local `portrait` beside its portable
        /// `avatar`; it never replaces `avatar`, so a file:// URL never reaches the server copy.
        public static string BoardJson(IEnumerable<PlaylistScoreRow> rows, string scoring, Func<string, string> portraitOf = null)
        {
            var list = Ranked(rows, scoring);
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"rank\":").Append(i + 1).Append(",\"id\":\"").Append(PlaylistRunRules.Esc(r.id))
                  .Append("\",\"name\":\"").Append(PlaylistRunRules.Esc(string.IsNullOrEmpty(r.name) ? r.id : r.name))
                  .Append("\",\"kind\":\"").Append(r.kind ?? PlaylistScoreRow.KindPlayer).Append('"');
                if (!string.IsNullOrEmpty(r.avatar)) sb.Append(",\"avatar\":\"").Append(PlaylistRunRules.Esc(r.avatar)).Append('"');
                if (!string.IsNullOrEmpty(r.color)) sb.Append(",\"color\":\"").Append(PlaylistRunRules.Esc(r.color)).Append('"');
                if (r.kind == PlaylistScoreRow.KindCpu)
                {
                    sb.Append(",\"tag\":\"").Append(PlaylistRunRules.Esc(string.IsNullOrEmpty(r.tag) ? CpuRoster.DefaultTag : r.tag)).Append('"');
                    if (!string.IsNullOrEmpty(r.title)) sb.Append(",\"title\":\"").Append(PlaylistRunRules.Esc(r.title)).Append('"');
                    if (!string.IsNullOrEmpty(r.difficulty)) sb.Append(",\"difficulty\":\"").Append(PlaylistRunRules.Esc(r.difficulty)).Append('"');
                    if (r.champion) sb.Append(",\"champion\":true");
                    string portrait = null;
                    if (portraitOf != null) try { portrait = portraitOf(r.id); } catch (Exception) { portrait = null; }
                    if (!string.IsNullOrEmpty(portrait) && portrait != r.avatar) sb.Append(",\"portrait\":\"").Append(PlaylistRunRules.Esc(portrait)).Append('"');
                }
                sb.Append(",\"score\":").Append(PlaylistRunRules.Num(r.Total(scoring))).Append(",\"legs\":[");
                bool first = true;
                foreach (var kv in r.legs)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"leg\":").Append(kv.Key + 1).Append(",\"score\":").Append(PlaylistRunRules.Num(kv.Value));
                    if (r.places.TryGetValue(kv.Key, out int pl)) sb.Append(",\"place\":").Append(pl).Append(pl == 0 ? ",\"dnf\":true" : "");
                    if (r.medals.TryGetValue(kv.Key, out int md) && md > 0) sb.Append(",\"medal\":\"").Append(CpuRoster.MedalName(md)).Append('"');
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            return sb.Append(']').ToString();
        }
    }

    /// The live board of the current run on this device.
    public static class PlaylistScoreboard
    {
        public const string StateName = "dp.playlist.board/";
        static SharedState s_ss;
        static string s_runId;
        static int s_version = -1;
        static readonly Dictionary<string, PlaylistScoreRow> s_rows = new Dictionary<string, PlaylistScoreRow>(StringComparer.Ordinal);

        public static string RunId => s_runId;
        public static IReadOnlyDictionary<string, PlaylistScoreRow> Rows { get { Read(); return s_rows; } }

        /// Open the run's board (a new runId closes the previous run's).
        public static void Ensure(string runId)
        {
            if (string.IsNullOrEmpty(runId) || runId == s_runId) return;
            Close();
            s_runId = runId;
            try
            {
                s_ss = SharedState.Open(StateName + runId);
                if (s_ss != null) s_ss.Requested += OnRequested;
                if (s_ss != null) s_ss.MarkGameplay();   // a spectator phone (no rig) never writes the board (Oct 23 2026, platform-rig)
            }
            catch (Exception e) { s_ss = null; Debug.LogWarning("[Playlist] scoreboard solo: " + e.Message); }
        }

        public static void Close()
        {
            if (s_ss != null) { s_ss.Requested -= OnRequested; try { s_ss.Dispose(); } catch (Exception) { } }
            s_ss = null; s_runId = null; s_version = -1; s_rows.Clear();
        }

        /// A player's absolute score in a leg of this run. A CPU (a "cpu:" id or a declared one) scores the same way.
        public static void Report(int leg, string playerId, double n, string name)
        {
            if (string.IsNullOrEmpty(s_runId) || string.IsNullOrEmpty(playerId)) return;
            var j = new JSONObject();
            j.AddField("p", playerId); j.AddField("leg", leg); j.AddField("n", n); j.AddField("name", name ?? "");
            AddFace(j, playerId, null, name);
            if (s_ss != null) s_ss.Request("sc", j.Print());
            else OnRequested("sc", j.Print(), "local");
        }

        /// One entrant's placing in a leg (players AND CPUs). place NoPlace = a medal / points only; medal -1 = none given.
        public static void ReportPlacing(int leg, string id, string name, string kind, string avatar, int place, double? points, int medal = -1)
        {
            if (string.IsNullOrEmpty(s_runId) || string.IsNullOrEmpty(id)) return;
            var j = new JSONObject();
            j.AddField("p", id); j.AddField("leg", leg); j.AddField("name", name ?? ""); j.AddField("pl", place);
            if (points.HasValue) j.AddField("n", points.Value);
            if (medal >= 0) j.AddField("md", medal);
            if (!string.IsNullOrEmpty(kind)) j.AddField("k", kind);
            if (!string.IsNullOrEmpty(avatar)) j.AddField("av", avatar);
            AddFace(j, id, kind, name);
            if (s_ss != null) s_ss.Request("pl", j.Print());
            else OnRequested("pl", j.Print(), "local");
        }

        /// A declared CPU's face rides the report (the reporter's cast = every device's cast), so the leader's row has it
        /// even on a device that never registered it. A "cpu:" id is a CPU even without a declaration.
        static void AddFace(JSONObject j, string id, string kind, string name)
        {
            CpuEntrant e = CpuRoster.Get(id);
            if (e == null && !CpuRoster.IsCpuId(id) && kind != PlaylistScoreRow.KindCpu) return;
            if (!j.HasField("k")) j.AddField("k", PlaylistScoreRow.KindCpu);
            if (e == null) return;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(e.name)) j.SetField("name", e.name);
            if (!j.HasField("av") && !string.IsNullOrEmpty(e.avatar)) j.AddField("av", e.avatar);
            if (!string.IsNullOrEmpty(e.color)) j.AddField("co", e.color);
            if (!string.IsNullOrEmpty(e.tag)) j.AddField("tg", e.tag);
            if (!string.IsNullOrEmpty(e.title)) j.AddField("tt", e.title);
            if (!string.IsNullOrEmpty(e.difficulty)) j.AddField("df", e.difficulty);
            if (e.champion) j.AddField("ch", true);
        }

        static bool ApplyFace(PlaylistScoreRow row, JSONObject j)
        {
            var ch = j?.GetField("ch");
            return PlaylistScoreRules.ApplyFace(row, PlaylistRunPlan.Str(j, "k", null), PlaylistRunPlan.Str(j, "av", null), PlaylistRunPlan.Str(j, "co", null),
                PlaylistRunPlan.Str(j, "tg", null), PlaylistRunPlan.Str(j, "tt", null), PlaylistRunPlan.Str(j, "df", null),
                ch != null && ch.type == JSONObject.Type.Bool && ch.boolValue);
        }

        static void OnRequested(string kind, string json, string from)
        {
            if (kind == "pl")
            {
                var pj = SharedState.ParseFragment(json);
                string pid = PlaylistRunPlan.Str(pj, "p");
                if (string.IsNullOrEmpty(pid) || pid.Length > 60) return;
                Read();
                var prow = s_rows.TryGetValue(pid, out var pr0) ? pr0 : new PlaylistScoreRow { id = pid };
                double? pts = pj != null && pj.HasField("n") ? (double?)PlaylistRunPlan.Num(pj, "n") : null;
                int medal = pj != null && pj.HasField("md") ? (int)PlaylistRunPlan.Num(pj, "md") : -1;
                bool placed = PlaylistScoreRules.ApplyPlacing(prow, (int)PlaylistRunPlan.Num(pj, "leg"), (int)PlaylistRunPlan.Num(pj, "pl"), pts,
                    PlaylistRunPlan.Str(pj, "name"), PlaylistRunPlan.Str(pj, "k", null), PlaylistRunPlan.Str(pj, "av", null), medal);
                if (!(ApplyFace(prow, pj) | placed)) return;
                s_rows[pid] = prow;
                if (s_ss != null) s_ss.Set("p." + pid, prow.ToJson());
                return;
            }
            if (kind != "sc") return;
            var j = SharedState.ParseFragment(json);
            string id = PlaylistRunPlan.Str(j, "p");
            if (string.IsNullOrEmpty(id) || id.Length > 60) return;
            Read();
            var row = s_rows.TryGetValue(id, out var r) ? r : new PlaylistScoreRow { id = id };
            bool scored = PlaylistScoreRules.Apply(row, (int)PlaylistRunPlan.Num(j, "leg"), PlaylistRunPlan.Num(j, "n"), PlaylistRunPlan.Str(j, "name"));
            if (!(ApplyFace(row, j) | scored)) return;
            s_rows[id] = row;
            if (s_ss != null) s_ss.Set("p." + id, row.ToJson());
        }

        static void Read()
        {
            if (s_ss == null || s_ss.Version == s_version) return;
            s_version = s_ss.Version;
            foreach (var k in s_ss.Keys)
                if (k.StartsWith("p.", StringComparison.Ordinal))
                    s_rows[k.Substring(2)] = PlaylistScoreRow.FromJson(k.Substring(2), SharedState.ParseFragment(s_ss.GetJson(k)));
        }

        /// The ranked board for PLAYLIST_STATE (null when no run / nobody scored).
        public static string BoardJson(string scoring)
        {
            Read();
            return s_rows.Count == 0 ? null : PlaylistScoreRules.BoardJson(s_rows.Values, scoring, CpuRoster.PortraitUrl);
        }
    }
}
