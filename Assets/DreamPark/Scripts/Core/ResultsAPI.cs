// ─────────────────────────────────────────────────────────────────────────
//  ResultsAPI.cs — DreamPark SDK: game results (Docs/RESULTS.md)
//
//  What happened in ONE round of one game, CPUs included: a high-score list,
//  a race's finishing order, a match's final score, a co-op group's score.
//  The session page shows every result ("Race 1 / Race 2", "Match 1: Aidan vs
//  CPU 11 – 8"); a leaderboard exists only where the result says it makes sense
//  (`board`), else the portal shows Recent games.
//
//  Lua (any title script, any headset; old apps: `if dp.results then`):
//      local id = dp.results.report{ go = self.gameObject, round = R.e, kind = 'placement',
//          entrants = { { player = 'me', place = 1 }, { cpu = 'cpu:blue', place = 2 } } }
//
//  The game never passes WHERE or HOW it is played. From `go` the platform
//  stamps the content id, the attraction (the same slug the score / storage
//  paths use), the way to play (RunnerHost / dp.level.runner), the package kind
//  and the zone; ProfileAPI gives the park / shard / headset / guest; core
//  fills session / location / adventure through `Context`; the SDK leaves them
//  empty. `me` is the entrant that is this headset's player.
//
//  resultId = first 20 hex of sha1(parkId|sessionId-or-locationId|contentId|
//  attraction|zone|round): every headset that reports the same round computes
//  the same id and the server MERGES the reports (each adds its own identity to
//  its own entrant). Without a round, the reporter's headsetId + atMs join the
//  hash (no merge).
//
//  Wire: POST /app/profile/results, sent exactly like ScoreAPI.SubmitScoreNow
//  (ProfileAPI.AuthHeader, GatedPost behind ContentGate, guestId for an unbound
//  guest). Held while no identity and no guest id exists, and after a transient
//  failure: at most 16, each for 90 s from the call.
//
//  Lua bridge: one flat delegate (dp_results_report: LuaTable → string) plus a
//  Lua bootstrap, the same shape as dp.score / dp.cpu. No XLua wrapper is
//  generated for this class (no [LuaCallCSharp]), so it adds no codegen drift.
//  The pure parts (Normalize, ComputeResultId, BuildBody) take no UnityEngine
//  state and are tested offline (Tools/results-offline).
// ─────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Defective.JSON;
using UnityEngine;
using XLua;

namespace DreamPark.API
{
    /// <summary>Session / location / adventure for a result. Core fills <see cref="ResultsAPI.Context"/>;
    /// the SDK leaves every field empty.</summary>
    public sealed class ResultsContext
    {
        public string sessionId;
        public string locationId;
        public string adventureId;
    }

    /// <summary>One entrant of a reported round: a player (pid or "me") or a CPU (dp.cpu id).</summary>
    public sealed class ResultEntrant
    {
        // as reported
        public string player, cpu, name, medal;
        public double? score;
        public int? place, side, stars;
        public bool dnf;
        public SortedDictionary<string, double> stats;
        // filled by Normalize
        public string id, kind, tag, color, avatar;
    }

    public sealed class ResultSide
    {
        public double? score;
        public string name;
        public List<double> sets;
    }

    /// <summary>A round as the game reported it (dp.results.report's table, or C#).</summary>
    public sealed class ResultInput
    {
        public string round, kind, label, title, variant, order, unit;
        /// <summary>A game inside the title with its own leaderboard (Nov 2026). Null = the title itself (the default).
        /// <c>gameIsAttraction</c> (Lua <c>game = true</c>): the attraction this round was played in is the game.
        /// Otherwise <c>game</c> is an attraction's prefab name ("A_Ponk") or a plain id ("boss-rush", with <c>gameName</c>).</summary>
        public string game, gameName;
        public bool gameIsAttraction;
        /// <summary>Set by Validate: the attraction slug the game is (game = true), for its name and picture.</summary>
        public string gameAttraction;
        /// <summary>"best" | "none" | "wins" | "stat" (then BoardStat / BoardOrder / BoardUnit). Null = the kind's default.</summary>
        public string board;
        public string boardStat, boardOrder, boardUnit;
        public List<ResultEntrant> entrants = new List<ResultEntrant>();
        public List<ResultSide> sides = new List<ResultSide>();
    }

    /// <summary>Everything the platform stamps on a result (CONTRACT §2).</summary>
    public sealed class ResultStamp
    {
        public string contentId = "", attraction = "", runnerId = "", runnerName = "", packageKind = "", zone = "";
        /// <summary>The attraction's prefab name (the last part of its resourceName, "A_Ponk"); "" when unknown.
        /// `game = true` takes its id from this: the slug cannot give it back ("a_ponk" slugs to "a__ponk").</summary>
        public string attractionName = "";
        public string parkId = "", shardId = "", locationId = "", sessionId = "", adventureId = "";
        public string headsetId = "", guestId;
        /// <summary>This headset's player id (PlayerPresence.MyId) and display name.</summary>
        public string myPlayerId = "", myName;
        public long atMs;
        /// <summary>A player id → its display name (peers), or null.</summary>
        public Func<string, string> playerName;
        /// <summary>A CPU id → its declared face (CpuRoster), or null.</summary>
        public Func<string, CpuEntrant> cpu;
    }

    public static class ResultsAPI
    {
        public static readonly string[] Kinds = { "score", "placement", "match", "coop" };
        static readonly string[] BaseUnits = { "points", "time", "distance", "percent" };
        static readonly string[] Medals = { "gold", "silver", "bronze" };

        // Caps (mirrored by DreamPark-Web lib/gameResults.js CAPS).
        public const int MaxEntrants = 16, MaxSides = 16, MaxSets = 9, MaxStats = 6, MaxPlace = 64;
        public const int MaxRound = 64, MaxLabel = 24, MaxTitle = 48, MaxVariant = 40, MaxUnit = 24, MaxName = 32, MaxGame = 64, MaxGameName = 48,
            MaxTag = 16, MaxRunnerName = 48, MaxBodyBytes = 8192, GuestIdReserveBytes = 160;
        const double MaxAbs = 1e12;

        static readonly Regex StatKeyRe = new Regex("^[a-z0-9_]{1,24}$");
        // A unit word reads before the number ("wave" → "Wave 12"); with a '#' it says where the number goes and "(s)"
        // marks the plural ("# stroke(s)" → "72 strokes", "1 stroke"). Nov 2026; DreamPark-Web mirrors it.
        static readonly Regex UnitWordRe = new Regex("^[a-z#][a-z0-9 _#()-]{0,23}$");
        static readonly Regex ColorRe = new Regex("^#?[0-9a-fA-F]{3,8}$");
        static readonly Regex IdCharRe = new Regex("[^A-Za-z0-9_.@-]");
        // A CPU id may also keep ':' (Web's entrant id allows it), so "cpu:team:red" stays what CpuRoster calls it.
        static readonly Regex CpuIdCharRe = new Regex("[^A-Za-z0-9_:.@-]");
        static readonly Regex HttpsRe = new Regex("^https://[^\\s\"'<>]{1,1000}$", RegexOptions.IgnoreCase);

        /// <summary>Core: session / location / adventure of the current visit. Null (SDK) = all empty.</summary>
        [BlackList] public static Func<ResultsContext> Context;
        /// <summary>Core: this device's player name for people (PlayerPresence.LocalPlayerName). Null = ProfileAPI.DisplayName.</summary>
        [BlackList] public static Func<string> LocalName;
        /// <summary>Every [Results] line goes through here (tests replace it).</summary>
        [BlackList] public static Action<string> Log = m => Debug.Log(m);
        /// <summary>Raised with each result body accepted for sending (before it goes out). Editor harnesses read it.</summary>
        [BlackList] public static event Action<JSONObject> Reported;

        // ── Pure ──────────────────────────────────────────────────────────

        /// <summary>Validate and normalize a round in place (CONTRACT §1): defaults, caps, ids, CPU faces,
        /// computed places. Returns null when it is valid, else why it was rejected.</summary>
        public static string Normalize(ResultInput r, ResultStamp s)
        {
            if (r == null) return "no result table";
            s = s ?? new ResultStamp();

            string kind = (r.kind ?? "").Trim().ToLowerInvariant();
            if (kind.Length == 0) kind = "score";
            if (Array.IndexOf(Kinds, kind) < 0) return $"kind '{r.kind}' is not score, placement, match or coop";
            r.kind = kind;

            r.round = CleanText(r.round, MaxRound);
            r.label = CleanText(r.label, MaxLabel) ?? DefaultLabel(kind);
            r.title = CleanText(r.title, MaxTitle);
            r.variant = CleanText(r.variant, MaxVariant);
            r.order = CleanOrder(r.order);

            // The game inside the title: its id is lower-case, and an attraction's id is its prefab name (the slug's last
            // part), so game = true in A_Ponk and game = 'A_Ponk' name the same board.
            // The id comes from the prefab name, never the slug's last part: Slugify turns '_' into "__" too, so the slug
            // of "…/A_Ponk" ends in "a__ponk" and its last part would be "ponk" (not 'A_Ponk''s "a_ponk").
            r.gameAttraction = null;
            string slug = (s.attraction ?? "").Trim();
            string here = GameIdOf(s.attractionName);
            if (r.gameIsAttraction)
            {
                if (slug.Length == 0) return "game = true needs go inside an attraction (this one has none)";
                r.gameAttraction = slug;
                if (here == null) { int cut = slug.LastIndexOf("__", StringComparison.Ordinal); here = GameIdOf(cut >= 0 ? slug.Substring(cut + 2) : slug); }
                r.game = here;
            }
            else
            {
                r.game = GameIdOf(r.game);
                // 'A_Ponk' reported from inside A_Ponk is exactly game = true (same body).
                if (r.game != null && slug.Length > 0 && r.game == here) r.gameAttraction = slug;
            }
            r.gameName = r.game == null ? null : CleanText(r.gameName, MaxGameName);

            string unit = CleanUnit(r.unit);
            if (unit == null) return $"unit '{r.unit}' is not points, time, distance, percent or a word of at most {MaxUnit} letters ('wave', or '# stroke(s)' with the number at '#')";
            r.unit = unit;

            string boardErr = NormalizeBoard(r);
            if (boardErr != null) return boardErr;

            if (r.sides == null) r.sides = new List<ResultSide>();
            if (r.sides.Count > MaxSides) return $"at most {MaxSides} sides";
            if (kind == "match" && r.sides.Count < 2) return "a match needs 2 or more sides";
            for (int i = 0; i < r.sides.Count; i++)
            {
                var sd = r.sides[i] ?? (r.sides[i] = new ResultSide());
                if (sd.score.HasValue && !Finite(sd.score.Value)) return $"sides[{i + 1}].score must be a finite number";
                if (sd.score.HasValue) sd.score = Clamp(sd.score.Value);
                sd.name = CleanText(sd.name, MaxName);
                if (sd.sets != null)
                {
                    if (sd.sets.Count > MaxSets) return $"sides[{i + 1}].sets: at most {MaxSets} numbers";
                    for (int k = 0; k < sd.sets.Count; k++)
                    {
                        if (!Finite(sd.sets[k])) return $"sides[{i + 1}].sets must be finite numbers";
                        sd.sets[k] = Clamp(sd.sets[k]);
                    }
                    if (sd.sets.Count == 0) sd.sets = null;
                }
            }

            if (r.entrants == null || r.entrants.Count < 1 || r.entrants.Count > MaxEntrants)
                return $"entrants must list 1..{MaxEntrants} players and CPUs";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < r.entrants.Count; i++)
            {
                var e = r.entrants[i];
                if (e == null) return $"entrants[{i + 1}] must be a table";
                string err = NormalizeEntrant(e, i, r.sides.Count, kind, s);
                if (err != null) return err;
                if (!ids.Add(e.id)) return $"entrant '{e.id}' is listed twice";
            }

            // Places the game left out (CONTRACT §1): placement → entrant order (DNF unplaced);
            // score / coop → from the values by `order`, ties share. Only when NO entrant has a place.
            if (!r.entrants.Any(e => e.place.HasValue))
            {
                if (kind == "placement")
                {
                    int p = 0;
                    foreach (var e in r.entrants) if (!e.dnf) e.place = ++p;
                }
                else if (kind == "score" || kind == "coop") PlacesFromScores(r.entrants, r.order);
            }
            return null;
        }

        /// <summary>Index of this headset's entrant (its player id), -1 when it has none.</summary>
        public static int MeIndex(ResultInput r, string myPlayerId)
        {
            if (r?.entrants == null || string.IsNullOrEmpty(myPlayerId)) return -1;
            string mine = "p:" + CleanPid(myPlayerId);
            for (int i = 0; i < r.entrants.Count; i++)
                if (r.entrants[i].kind == "player" && r.entrants[i].id == mine) return i;
            return -1;
        }

        /// <summary>CONTRACT §2: first 20 hex of sha1(parkId|sessionId-or-locationId|contentId|attraction|zone|round),
        /// plus |headsetId|atMs when there is no round. Identical to DreamPark-Web computeResultId.</summary>
        public static string ComputeResultId(string parkId, string sessionId, string locationId, string contentId,
            string attraction, string zone, string round, string headsetId, long atMs)
        {
            // With a round, the id is park + title + attraction + zone + round ONLY (Nov 10 review): the session and
            // location are left out because one headset can know them before another does, which split one round into
            // two results. The round value must therefore be unique on its own (an epoch + a shared start time or a
            // leader-minted id, never a small counter alone). "" and "main" are the same zone.
            string z = string.IsNullOrEmpty(zone) ? "main" : zone;
            var sb = new StringBuilder();
            sb.Append(parkId ?? "").Append('|');
            if (string.IsNullOrEmpty(round))
                sb.Append(!string.IsNullOrEmpty(sessionId) ? sessionId : (locationId ?? "")).Append('|');
            sb.Append(contentId ?? "").Append('|')
              .Append(attraction ?? "").Append('|')
              .Append(z).Append('|')
              .Append(round ?? "");
            if (string.IsNullOrEmpty(round))
                sb.Append('|').Append(headsetId ?? "").Append('|').Append(atMs.ToString(CultureInfo.InvariantCulture));
            byte[] hash;
            using (var sha = SHA1.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(40);
            for (int i = 0; i < hash.Length; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString(0, 20);
        }

        public static string ComputeResultId(ResultInput r, ResultStamp s)
            => ComputeResultId(s.parkId, s.sessionId, s.locationId, s.contentId, s.attraction, s.zone, r.round, s.headsetId, s.atMs);

        /// <summary>The POST body (CONTRACT §3) for a normalized round. `guestId` is added only for an unbound guest.</summary>
        public static JSONObject BuildBody(ResultInput r, ResultStamp s, string resultId)
        {
            var res = new JSONObject(JSONObject.Type.Object);
            res.AddField("resultId", resultId ?? "");
            res.AddField("round", r.round ?? "");
            res.AddField("contentId", s.contentId ?? "");
            res.AddField("attraction", s.attraction ?? "");
            res.AddField("runnerId", s.runnerId ?? "");
            res.AddField("runnerName", s.runnerName ?? "");
            res.AddField("packageKind", s.packageKind ?? "");
            res.AddField("zone", s.zone ?? "");
            res.AddField("kind", r.kind);
            res.AddField("label", r.label ?? "");
            if (!string.IsNullOrEmpty(r.title)) res.AddField("title", r.title);
            if (!string.IsNullOrEmpty(r.variant)) res.AddField("variant", r.variant);
            if (!string.IsNullOrEmpty(r.game))
            {
                res.AddField("game", r.game);
                if (!string.IsNullOrEmpty(r.gameName)) res.AddField("gameName", r.gameName);
                if (!string.IsNullOrEmpty(r.gameAttraction)) res.AddField("gameAttraction", r.gameAttraction);
            }
            res.AddField("order", r.order);
            res.AddField("unit", r.unit);
            if (r.board == "stat")
            {
                var b = new JSONObject(JSONObject.Type.Object);
                b.AddField("stat", r.boardStat);
                b.AddField("order", r.boardOrder);
                b.AddField("unit", r.boardUnit);
                res.AddField("board", b);
            }
            else res.AddField("board", r.board);

            var ents = new JSONObject(JSONObject.Type.Array);
            foreach (var e in r.entrants)
            {
                var j = new JSONObject(JSONObject.Type.Object);
                j.AddField("id", e.id);
                j.AddField("kind", e.kind);
                if (!string.IsNullOrEmpty(e.name)) j.AddField("name", e.name);
                if (e.score.HasValue) AddNum(j, "score", e.score.Value);
                if (e.place.HasValue) j.AddField("place", e.place.Value);
                if (e.dnf) j.AddField("dnf", true);
                if (e.side.HasValue) j.AddField("side", e.side.Value);
                if (!string.IsNullOrEmpty(e.medal)) j.AddField("medal", e.medal);
                if (e.stars.HasValue) j.AddField("stars", e.stars.Value);
                if (e.stats != null && e.stats.Count > 0)
                {
                    var st = new JSONObject(JSONObject.Type.Object);
                    foreach (var kv in e.stats) AddNum(st, kv.Key, kv.Value);
                    j.AddField("stats", st);
                }
                if (!string.IsNullOrEmpty(e.tag)) j.AddField("tag", e.tag);
                if (!string.IsNullOrEmpty(e.color)) j.AddField("color", e.color);
                if (!string.IsNullOrEmpty(e.avatar)) j.AddField("avatar", e.avatar);
                ents.Add(j);
            }
            res.AddField("entrants", ents);

            var sides = new JSONObject(JSONObject.Type.Array);
            foreach (var sd in r.sides)
            {
                var j = new JSONObject(JSONObject.Type.Object);
                if (sd.score.HasValue) AddNum(j, "score", sd.score.Value);
                if (!string.IsNullOrEmpty(sd.name)) j.AddField("name", sd.name);
                if (sd.sets != null)
                {
                    var sets = new JSONObject(JSONObject.Type.Array);
                    foreach (double v in sd.sets) sets.Add(NumJson(v));
                    j.AddField("sets", sets);
                }
                sides.Add(j);
            }
            res.AddField("sides", sides);

            res.AddField("me", MeIndex(r, s.myPlayerId));
            res.AddField("atMs", s.atMs);
            res.AddField("parkId", s.parkId ?? "");
            res.AddField("shardId", s.shardId ?? "");
            res.AddField("locationId", s.locationId ?? "");
            res.AddField("sessionId", s.sessionId ?? "");
            res.AddField("adventureId", s.adventureId ?? "");

            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("result", res);
            body.AddField("headsetId", s.headsetId ?? "");
            if (!string.IsNullOrEmpty(s.guestId)) body.AddField("guestId", s.guestId);
            return body;
        }

        // ── Pure helpers ──────────────────────────────────────────────────

        /// <summary>Over the server's 8 KB cap once SendOrHold adds a guestId (≤ 128 chars + its key)? `bytes` = UTF-8 size now.</summary>
        public static bool BodyTooLarge(JSONObject body, out int bytes)
        {
            bytes = body == null ? 0 : Encoding.UTF8.GetByteCount(body.ToString());
            return bytes > MaxBodyBytes - GuestIdReserveBytes;
        }

        static string DefaultLabel(string kind)
            => kind == "placement" ? "Race" : kind == "match" ? "Match" : "Game";

        static string DefaultBoard(string kind)
            => kind == "placement" || kind == "match" ? "none" : "best";

        static string NormalizeBoard(ResultInput r)
        {
            string b = (r.board ?? "").Trim().ToLowerInvariant();
            if (b.Length == 0 && !string.IsNullOrEmpty(r.boardStat)) b = "stat";
            if (b.Length == 0) { r.board = DefaultBoard(r.kind); return null; }
            if (b == "best" || b == "none" || b == "wins") { r.board = b; return null; }
            if (b != "stat") return $"board '{r.board}' must be 'best', 'none', 'wins' or {{stat, order, unit}}";
            string stat = (r.boardStat ?? "").Trim();
            if (!StatKeyRe.IsMatch(stat)) return "board.stat must match [a-z0-9_]{1,24}";
            string unit = CleanUnit(r.boardUnit);
            if (unit == null) return $"board.unit '{r.boardUnit}' is not a unit";
            r.board = "stat";
            r.boardStat = stat;
            r.boardOrder = CleanOrder(r.boardOrder);
            r.boardUnit = unit;
            return null;
        }

        static string NormalizeEntrant(ResultEntrant e, int i, int sidesCount, string kind, ResultStamp s)
        {
            string n = $"entrants[{i + 1}]";
            bool isCpu = !string.IsNullOrEmpty(e.cpu) && string.IsNullOrEmpty(e.player);
            if (!isCpu && !string.IsNullOrEmpty(e.player) && CpuRoster.IsCpuId(e.player)) { isCpu = true; e.cpu = e.player; }
            if (isCpu)
            {
                // CpuRoster's spelling ("cpu:<lower-case, spaces → '-'>"), so the id and the face lookup match the cast;
                // only characters Web's entrant id cannot hold are dropped from the wire id.
                string norm = CpuRoster.Normalize(e.cpu);
                string cid = CpuRoster.Prefix + CpuIdCharRe.Replace(norm.Length > CpuRoster.Prefix.Length ? norm.Substring(CpuRoster.Prefix.Length) : "", "");
                if (cid.Length <= CpuRoster.Prefix.Length) return $"{n}: cpu '{e.cpu}' is not a CPU id";
                e.id = cid;
                e.kind = "cpu";
                CpuEntrant face = null;
                try { face = s.cpu?.Invoke(norm) ?? (cid != norm ? s.cpu?.Invoke(cid) : null); } catch (Exception) { face = null; }
                if (string.IsNullOrWhiteSpace(e.name)) e.name = face?.name;
                e.tag = CleanText(face?.tag, MaxTag) ?? CpuRoster.DefaultTag;
                string color = face?.color?.Trim();
                e.color = !string.IsNullOrEmpty(color) && ColorRe.IsMatch(color) ? (color.StartsWith("#") ? color : "#" + color) : null;
                string avatar = face?.avatar?.Trim();
                e.avatar = !string.IsNullOrEmpty(avatar) && HttpsRe.IsMatch(avatar) ? avatar : null;
                if (string.IsNullOrWhiteSpace(e.name)) e.name = cid.Substring(CpuRoster.Prefix.Length).ToUpperInvariant();
            }
            else
            {
                string pid = (e.player ?? "").Trim();
                if (pid.Length == 0) return $"{n} needs player = <id> or 'me', or cpu = <dp.cpu id>";
                bool isMe = pid == "me" || (!string.IsNullOrEmpty(s.myPlayerId) && pid == s.myPlayerId);
                if (pid == "me")
                {
                    if (string.IsNullOrEmpty(s.myPlayerId)) return $"{n}: player 'me' but this device has no player id";
                    pid = s.myPlayerId;
                }
                pid = CleanPid(pid);
                if (pid.Length == 0) return $"{n}: player '{e.player}' is not a player id";
                e.id = "p:" + pid;
                e.kind = "player";
                e.tag = null; e.color = null; e.avatar = null;
                if (string.IsNullOrWhiteSpace(e.name))
                {
                    if (isMe) e.name = s.myName;
                    else { try { e.name = s.playerName?.Invoke(pid); } catch (Exception) { e.name = null; } }
                }
            }
            e.name = CleanText(e.name, MaxName);

            if (e.score.HasValue)
            {
                if (!Finite(e.score.Value)) return $"{n}.score must be a finite number";
                e.score = Clamp(e.score.Value);
            }
            if (e.place.HasValue && (e.place.Value < 1 || e.place.Value > MaxPlace)) return $"{n}.place must be 1..{MaxPlace}";
            if (e.side.HasValue)
            {
                if (e.side.Value < 1 || e.side.Value > sidesCount) return $"{n}.side must index sides (1..{sidesCount})";
            }
            else if (kind == "match") return $"{n} needs a side in a match";
            if (e.medal != null)
            {
                string m = e.medal.Trim().ToLowerInvariant();
                e.medal = Array.IndexOf(Medals, m) >= 0 ? m : null;   // 'none' / unknown → no medal
            }
            if (e.stars.HasValue && (e.stars.Value < 0 || e.stars.Value > 5)) return $"{n}.stars must be 0..5";
            if (e.stats != null)
            {
                if (e.stats.Count > MaxStats) return $"{n}: at most {MaxStats} stats";
                foreach (var kv in e.stats.ToList())
                {
                    if (!StatKeyRe.IsMatch(kv.Key)) return $"{n}: stat key '{kv.Key}' must match [a-z0-9_]{{1,24}}";
                    if (!Finite(kv.Value)) return $"{n}: stat '{kv.Key}' must be a finite number";
                    e.stats[kv.Key] = Clamp(kv.Value);
                }
                if (e.stats.Count == 0) e.stats = null;
            }
            return null;
        }

        /// <summary>Best first by order, ties share a place (90, 90, 40 → 1, 1, 3); DNF / no value stay unplaced.</summary>
        static void PlacesFromScores(List<ResultEntrant> entrants, string order)
        {
            var scored = entrants.Select((e, i) => (e, i)).Where(x => x.e.score.HasValue && !x.e.dnf).ToList();
            scored.Sort((a, b) =>
            {
                int c = order == "low" ? a.e.score.Value.CompareTo(b.e.score.Value) : b.e.score.Value.CompareTo(a.e.score.Value);
                return c != 0 ? c : a.i.CompareTo(b.i);
            });
            int place = 0;
            for (int k = 0; k < scored.Count; k++)
            {
                if (k == 0 || scored[k].e.score.Value != scored[k - 1].e.score.Value) place = k + 1;
                scored[k].e.place = place;
            }
        }

        internal static string CleanText(string v, int max)
        {
            if (v == null) return null;
            var sb = new StringBuilder(v.Length);
            bool space = false;
            foreach (char c in v)
            {
                // Web's cleanText: control chars → space, then JS `\s+` → one space, trimmed.
                bool ws = (char.IsWhiteSpace(c) && c != '\u0085') || c < 0x20 || c == 0x7f || c == '﻿';
                if (ws) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            if (sb.Length == 0) return null;
            // Cut at `max` code points like Web's [...s].slice(0, max) (a surrogate pair is one, never split).
            int i = 0;
            for (int cps = 0; i < sb.Length && cps < max; i++, cps++)
                if (char.IsHighSurrogate(sb[i]) && i + 1 < sb.Length && char.IsLowSurrogate(sb[i + 1])) i++;
            return i < sb.Length ? sb.ToString(0, i) : sb.ToString();
        }

        /// <summary>Web's gameIdOf: trimmed, lower-case, only [a-z0-9 _.-], at most 64; null when nothing is left.</summary>
        internal static string GameIdOf(string v)
        {
            if (v == null) return null;
            var sb = new StringBuilder();
            foreach (char c in v.Trim().ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == '_' || c == '.' || c == '-') sb.Append(c);
            if (sb.Length > MaxGame) sb.Length = MaxGame;
            return sb.Length == 0 ? null : sb.ToString();
        }

        static string CleanPid(string pid)
        {
            string s = IdCharRe.Replace(pid ?? "", "");
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        static string CleanOrder(string o)
        {
            string s = (o ?? "").Trim().ToLowerInvariant();
            return s == "low" || s == "asc" ? "low" : "high";
        }

        /// <summary>The unit word, lower-cased ("points" when empty), or null when it is not a unit.</summary>
        static string CleanUnit(string u)
        {
            string s = (u ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return "points";
            if (Array.IndexOf(BaseUnits, s) >= 0) return s;
            if (s.Length > MaxUnit || !UnitWordRe.IsMatch(s)) return null;
            int hashes = 0; bool letter = false;
            foreach (char c in s) { if (c == '#') hashes++; else if (c >= 'a' && c <= 'z') letter = true; }
            return hashes <= 1 && letter ? s : null;
        }

        static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        static double Clamp(double d) => Math.Max(-MaxAbs, Math.Min(MaxAbs, d));

        static JSONObject NumJson(double v)
            => v == Math.Floor(v) && Math.Abs(v) < 9e15 ? new JSONObject((long)v) : new JSONObject(v);

        static void AddNum(JSONObject o, string k, double v)
        {
            if (v == Math.Floor(v) && Math.Abs(v) < 9e15) o.AddField(k, (long)v); else o.AddField(k, v);
        }

        /// <summary>A Lua round value as a string every headset spells the same (12 and 12.0 → "12").</summary>
        internal static string RoundText(object v)
        {
            switch (v)
            {
                case null: return null;
                case string str: return str;
                case bool b: return b ? "true" : "false";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case double d:
                    if (!Finite(d)) return null;
                    return d == Math.Floor(d) && Math.Abs(d) < 9e15
                        ? ((long)d).ToString(CultureInfo.InvariantCulture)
                        : d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return RoundText((double)f);
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // ── Where and how this is played (from `go`) ──────────────────────

        static bool SameGame(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
            string Strip(string x) => x.EndsWith("Beta", StringComparison.Ordinal) ? x.Substring(0, x.Length - 4) : x;
            return string.Equals(Strip(a), Strip(b), StringComparison.Ordinal);
        }

        /// <summary>The way to play running for `go` (or, with no go, the board this device renders), when it is
        /// `contentId`'s: runner id + name and the zone ("main" or a zone id). All "" when none.</summary>
        public static void ResolveRunner(GameObject go, string contentId, out string runnerId, out string runnerName, out string zone)
        {
            runnerId = ""; runnerName = ""; zone = "";
            RunnerDirector d = null;
            try { d = RunnerLua.CurrentDirector?.Invoke(); } catch (Exception) { d = null; }
            if (d == null || d.Runner == null) return;
            if (!SameGame(d.ContentId, contentId)) return;
            runnerId = d.Runner.runnerId ?? "";
            runnerName = CleanText(d.Runner.name, MaxRunnerName) ?? runnerId;
            zone = "main";
            int load = -1;
            try { load = go != null ? (RunnerLua.LoadIdOf?.Invoke(go) ?? -1) : -1; } catch (Exception) { load = -1; }
            try
            {
                string info = d.RunnerInfoJson(load);
                if (info == null && load >= 0) info = d.RunnerInfoJson(-1);
                var j = string.IsNullOrEmpty(info) ? null : new JSONObject(info);
                string z = j?.GetField("zone")?.stringValue;
                if (!string.IsNullOrEmpty(z)) zone = z;
            }
            catch (Exception) { /* a board read must never fail a report */ }
        }

        /// <summary>The way-to-play id for a score/storage write of `contentId` from `go` (null = this device's board).</summary>
        public static string RunnerIdFor(GameObject go, string contentId)
        {
            // Cheap on purpose (storage ops may call it every frame): one board is rendered per device, so the
            // way to play does not depend on which load `go` stands under; only ResolveRunner's zone does.
            RunnerDirector d = null;
            try { d = RunnerLua.CurrentDirector?.Invoke(); } catch (Exception) { d = null; }
            if (d == null || d.Runner == null || !SameGame(d.ContentId, contentId)) return "";
            return d.Runner.runnerId ?? "";
        }

        /// <summary>"playlist" | "arena" | "sequence" | "adventure" | "" for `go`.</summary>
        public static string PackageKindOf(GameObject go)
        {
            if (go == null) return "";
            try { if (PlaylistRunner.For(go) != null) return "playlist"; } catch (Exception) { }
            DreamParkPackageHost host = null;
            try { host = DreamParkLuaAPI.Package(go); } catch (Exception) { host = null; }
            if (host == null) return "";
            switch (host.kind)
            {
                case DreamParkPackageKind.Arena: return "arena";
                case DreamParkPackageKind.Adventure: return "adventure";
                default: return "sequence";
            }
        }

        /// <summary>Everything the platform stamps for a report from `go` (CONTRACT §2). Null when `go` belongs to no title.</summary>
        public static ResultStamp StampFor(GameObject go, out string error)
        {
            error = null;
            if (go == null) { error = "go = self.gameObject is required"; return null; }
            string contentId = null, attraction = "";
            try
            {
                var scope = GameStorageAPI.ResolveScopeTable(go);
                if (scope != null)
                {
                    contentId = scope.Get<string>("gameId");
                    attraction = scope.Get<string>("attraction") ?? "";
                    scope.Dispose();
                }
            }
            catch (Exception) { /* fall through to the title walk */ }
            if (string.IsNullOrEmpty(contentId)) { try { contentId = SharedState.TitleOf(go); } catch (Exception) { contentId = null; } }
            if (string.IsNullOrEmpty(contentId)) { error = $"'{go.name}' is in no attraction, prop, Player rig or package (no content id)"; return null; }

            var s = new ResultStamp { contentId = contentId, attraction = attraction ?? "", attractionName = AttractionNameOf(go, attraction) };
            ResolveRunner(go, contentId, out s.runnerId, out s.runnerName, out s.zone);
            s.packageKind = PackageKindOf(go);
            s.parkId = ProfileAPI.CallingParkId() ?? "";
            s.shardId = ProfileAPI.ShardContext ?? "";
            ResultsContext ctx = null;
            try { ctx = Context?.Invoke(); } catch (Exception e) { Log?.Invoke("[Results] Context hook threw: " + e.Message); }
            s.sessionId = ctx?.sessionId ?? "";
            s.locationId = ctx?.locationId ?? "";
            s.adventureId = ctx?.adventureId ?? "";
            s.headsetId = SystemInfo.deviceUniqueIdentifier ?? "";
            s.atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try { s.myPlayerId = PlayerPresence.MyId ?? ""; } catch (Exception) { s.myPlayerId = ""; }
            try { s.myName = LocalName != null ? LocalName() : ProfileAPI.DisplayName; } catch (Exception) { s.myName = null; }
            s.playerName = PeerName;
            s.cpu = CpuRoster.Get;
            return s;
        }

        /// <summary>The prefab name behind `slug` (the last part of the same resourceName GameStorageAPI.ResolveScopeTable
        /// slugged: GameArea first, then PropTemplate); "" when it cannot be confirmed.</summary>
        static string AttractionNameOf(GameObject go, string slug)
        {
            if (go == null || string.IsNullOrEmpty(slug)) return "";
            try
            {
                string rn = null;
                var area = go.GetComponentInParent<GameArea>(true);
                if (area != null && !string.IsNullOrEmpty(area.gameId)) rn = area.resourceName;
                else
                {
                    var prop = go.GetComponentInParent<PropTemplate>(true);
                    if (prop != null && !string.IsNullOrEmpty(prop.gameId)) rn = prop.resourceName;
                }
                if (string.IsNullOrEmpty(rn) || GameStorageAPI.Slugify(rn) != slug) return "";
                return LeafName(rn);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>"ArcadeLand/Levels/Large/A_Ponk" → "A_Ponk".</summary>
        public static string LeafName(string resourceName)
        {
            string rn = (resourceName ?? "").Trim();
            int cut = rn.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? rn.Substring(cut + 1).Trim() : rn;
        }

        static string PeerName(string pid)
        {
            var pp = PlayerPresence.Instance;
            if (pp != null && pp.Peers != null && pp.Peers.TryGetValue(pid, out var peer) && peer != null && !string.IsNullOrEmpty(peer.displayName))
                return peer.displayName;
            try { var n = RunnerHost.PlayerName?.Invoke(pid); if (!string.IsNullOrEmpty(n) && n != pid) return n; } catch (Exception) { }
            return null;
        }

        // ── Report ────────────────────────────────────────────────────────

        /// <summary>Report one round from `go`. Returns the result id, or null when it was rejected (logged).</summary>
        public static string Report(GameObject go, ResultInput r)
        {
            var s = StampFor(go, out string err);
            if (s == null) { Log?.Invoke("[Results] report rejected: " + err); return null; }
            err = Normalize(r, s);
            if (err != null) { Log?.Invoke($"[Results] report rejected ({s.contentId}): {err}"); return null; }
            string id = ComputeResultId(r, s);
            var body = BuildBody(r, s, id);
            if (BodyTooLarge(body, out int bytes)) { Log?.Invoke($"[Results] report rejected ({s.contentId}): {bytes} bytes is over the {MaxBodyBytes - GuestIdReserveBytes}-byte cap"); return null; }
            try { Reported?.Invoke(body); } catch (Exception e) { Log?.Invoke("[Results] Reported handler threw: " + e.Message); }
            Log?.Invoke($"[Results] {r.kind} '{r.label}' {s.contentId}/{(s.attraction.Length > 0 ? s.attraction : "-")} way='{s.runnerId}' round='{r.round}' → {id} ({r.entrants.Count} entrants, me={MeIndex(r, s.myPlayerId)})");
            Send(body, s.contentId);
            return id;
        }

        // ── Sending (ScoreAPI.SubmitScoreNow's path) ──────────────────────

        const int MaxPending = 16;
        const float PendingTtlSec = 90f;
        const int RetryDelayMs = 10000;

        struct Pending { public JSONObject body; public string contentId; public float atSec; }
        static readonly List<Pending> _pending = new List<Pending>();
        static bool _hooked, _retryScheduled;

        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            ProfileAPI.OnIdentityBound += FlushPending;
            // The player left: a held result must never be filed under whoever picks the headset up next.
            ProfileAPI.OnIdentityCleared += () =>
            {
                if (_pending.Count > 0) Log?.Invoke($"[Results] dropping {_pending.Count} held result(s): identity cleared");
                _pending.Clear();
            };
        }

        static void Send(JSONObject body, string contentId)
            => SendOrHold(new Pending { body = body, contentId = contentId, atSec = Time.realtimeSinceStartup });

        static void SendOrHold(Pending p)
        {
            Hook();
            string guestId = null;
            if (!ProfileAPI.IsBound)
            {
                guestId = ProfileAPI.GuestWriteId;
                if (guestId == null) { Hold(p, "no identity bound yet"); return; }
            }
            if (guestId != null) p.body.SetField("guestId", guestId); else p.body.RemoveField("guestId");
            ProfileAPI.GatedPost("/app/profile/results", ProfileAPI.AuthHeader(), p.body, (ok, resp) =>
            {
                if (ok) return;
                long code = resp?.statusCode ?? 0;
                if (code == 400 || code == 401 || code == 403 || code == 404 || code == 409 || code == 413)
                {
                    Log?.Invoke($"[Results] server refused a result ({code}): {resp?.error ?? resp?.rawText}");
                    return;
                }
                Hold(p, code == 0 ? "network error" : "HTTP " + code);
                ScheduleRetry();
            });
        }

        static void Hold(Pending p, string why)
        {
            if (Time.realtimeSinceStartup - p.atSec > PendingTtlSec) { Log?.Invoke($"[Results] dropped a result: {why} for over {PendingTtlSec:0} s"); return; }
            if (_pending.Count >= MaxPending) { Log?.Invoke($"[Results] dropped a result: {why} and {MaxPending} are already held"); return; }
            _pending.Add(p);
            Log?.Invoke($"[Results] holding a result: {why} ({_pending.Count} held)");
        }

        static void FlushPending()
        {
            if (_pending.Count == 0) return;
            var held = _pending.ToArray();
            _pending.Clear();
            foreach (var p in held) SendOrHold(p);
        }

        static async void ScheduleRetry()
        {
            if (_retryScheduled) return;
            _retryScheduled = true;
            try { await System.Threading.Tasks.Task.Delay(RetryDelayMs); }
            catch (Exception) { }
            _retryScheduled = false;
            if (Application.isPlaying) FlushPending();
        }

        // ── Lua bridge ────────────────────────────────────────────────────

        /// <summary>Read dp.results.report's table into a ResultInput (+ its go). Returns null with `error` when it cannot.</summary>
        public static ResultInput FromLua(LuaTable t, out GameObject go, out string error)
        {
            go = null; error = null;
            if (t == null) { error = "dp.results.report needs a table"; return null; }
            object g = t.Get<object>("go");
            // A destroyed Component's .gameObject throws; Unity's == null catches it (StampFor then rejects the null go).
            go = g as GameObject ?? (g is Component comp && comp != null ? comp.gameObject : null);
            // round is the merge key: a table or an object would print the same text on every round ("XLua.LuaTable")
            // and merge unrelated rounds into one result, so only a string, number or boolean is a round.
            object roundRaw = t.Get<object>("round");
            if (roundRaw != null && !IsScalar(roundRaw)) { error = "round must be a string or a number every headset agrees on"; return null; }
            var r = new ResultInput
            {
                round = RoundText(roundRaw),
                kind = Str(t, "kind"), label = Str(t, "label"), title = Str(t, "title"), variant = Str(t, "variant"),
                order = Str(t, "order"), unit = Str(t, "unit"), gameName = Str(t, "gameName"),
            };
            object gv = t.Get<object>("game");
            if (gv is bool gb) r.gameIsAttraction = gb;
            else if (gv is string gs) r.game = gs;
            else if (gv != null) { error = "game must be true (this attraction), an attraction's prefab name or a plain id"; return null; }
            object board = t.Get<object>("board");
            if (board is string bs) r.board = bs;
            else if (board is LuaTable bt) { r.board = "stat"; r.boardStat = Str(bt, "stat"); r.boardOrder = Str(bt, "order"); r.boardUnit = Str(bt, "unit"); }
            else if (board != null) { error = "board must be 'best', 'none', 'wins' or {stat, order, unit}"; return null; }

            var et = t.Get<object>("entrants") as LuaTable;
            if (et == null) { error = "entrants must be a list"; return null; }
            int n = et.Length;
            if (n > MaxEntrants) { error = $"entrants must list 1..{MaxEntrants} players and CPUs"; return null; }
            for (int i = 1; i <= n; i++)
            {
                var row = et.Get<int, object>(i) as LuaTable;
                if (row == null) { error = $"entrants[{i}] must be a table"; return null; }
                object pv = row.Get<object>("player"), cv = row.Get<object>("cpu");
                if ((pv != null && !IsScalar(pv)) || (cv != null && !IsScalar(cv)))
                { error = $"entrants[{i}]: player / cpu must be an id string (e.g. p.id from dp.peers(), not the peer table)"; return null; }
                var e = new ResultEntrant
                {
                    player = Str(row, "player"), cpu = Str(row, "cpu"), name = Str(row, "name"), medal = Str(row, "medal"),
                    dnf = row.Get<object>("dnf") is bool dnf && dnf,
                };
                if (!Num(row, "score", out e.score, out error) || !Int(row, "place", out e.place, out error) ||
                    !Int(row, "side", out e.side, out error) || !Int(row, "stars", out e.stars, out error))
                { error = $"entrants[{i}].{error}"; return null; }
                if (row.Get<object>("stats") is LuaTable stats)
                {
                    e.stats = new SortedDictionary<string, double>(StringComparer.Ordinal);
                    foreach (object k in stats.GetKeys())
                    {
                        object v = stats.Get<object, object>(k);
                        if (!(k is string key)) { error = $"entrants[{i}].stats keys must be names"; return null; }
                        if (!ToDouble(v, out double d)) { error = $"entrants[{i}].stats.{key} must be a number"; return null; }
                        e.stats[key] = d;
                    }
                }
                row.Dispose();
                r.entrants.Add(e);
            }
            et.Dispose();

            if (t.Get<object>("sides") is LuaTable st)
            {
                int sn = st.Length;
                if (sn > MaxSides) { error = $"at most {MaxSides} sides"; return null; }
                for (int i = 1; i <= sn; i++)
                {
                    var row = st.Get<int, object>(i) as LuaTable;
                    var sd = new ResultSide();
                    if (row != null)
                    {
                        if (!Num(row, "score", out sd.score, out error)) { error = $"sides[{i}].{error}"; return null; }
                        sd.name = Str(row, "name");
                        if (row.Get<object>("sets") is LuaTable sets)
                        {
                            sd.sets = new List<double>();
                            int m = Math.Min(sets.Length, MaxSets + 1);
                            for (int k = 1; k <= m; k++)
                            {
                                if (!ToDouble(sets.Get<int, object>(k), out double d)) { error = $"sides[{i}].sets must be numbers"; return null; }
                                sd.sets.Add(d);
                            }
                        }
                        row.Dispose();
                    }
                    r.sides.Add(sd);
                }
            }
            return r;
        }

        static string Str(LuaTable t, string k)
        {
            object v = t.Get<object>(k);
            return v == null ? null : v is string s ? s : RoundText(v);
        }

        static bool ToDouble(object v, out double d)
        {
            switch (v)
            {
                case double x: d = x; return true;
                case long l: d = l; return true;
                case int i: d = i; return true;
                case float f: d = f; return true;
                case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            }
            d = 0; return false;
        }

        static bool Num(LuaTable t, string k, out double? value, out string error)
        {
            value = null; error = null;
            object v = t.Get<object>(k);
            if (v == null) return true;
            if (!ToDouble(v, out double d) || !Finite(d)) { error = k + " must be a finite number"; return false; }
            value = d; return true;
        }

        static bool Int(LuaTable t, string k, out int? value, out string error)
        {
            value = null;
            if (!Num(t, k, out double? d, out error)) return false;
            // JS Math.round (floor(x + 0.5)), as Web's cleanInt: 2.5 → 3, -0.5 → 0.
            if (d.HasValue) value = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Floor(d.Value + 0.5)));
            return true;
        }

        static bool IsScalar(object v) => v is string || v is bool || v is double || v is long || v is int || v is float;

        static string ReportFromLua(LuaTable t)
        {
            try
            {
                var r = FromLua(t, out GameObject go, out string err);
                if (r == null) { Log?.Invoke("[Results] report rejected: " + err); return ""; }
                return Report(go, r) ?? "";
            }
            catch (Exception e)
            {
                Log?.Invoke("[Results] report threw: " + e);
                return "";
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua()
        {
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;
                env.Global.Set("dp_results_report", new Func<LuaTable, string>(ReportFromLua));
                env.DoString(@"
                    dp = dp or {}
                    dp.results = {
                        features = { kinds = { 'score', 'placement', 'match', 'coop' }, units = true, variants = true, merge = true, games = true },
                        -- t = { go = self.gameObject, round, kind, label, title, variant, game, gameName, order, unit, board, entrants, sides }
                        -- -> the result id, or nil when it was rejected (the console says why). Docs/RESULTS.md.
                        report = function(t)
                            if type(t) ~= 'table' then print('[Results] dp.results.report needs a table') return nil end
                            local id = dp_results_report(t)
                            if id == nil or id == '' then return nil end
                            return id
                        end,
                    }
                ", "dp.results.bootstrap");
            }
            catch (Exception e)
            {
                Debug.LogError("[Results] Failed to register Lua bridge: " + e);
            }
        }
    }
}
