// ─────────────────────────────────────────────────────────────────────
//  PlaylistAutoRules.cs — AUTO-DETECTION of playlist objectives for titles
//  that never call dp.playlist (Docs/PLAYLISTS.md § 13). Pure rules; the
//  glue is PlaylistAutoSignals.
//
//  Aidan: "does it require changes to the games themselves?" — a title opts
//  in today with a few dp.playlist lines (§ 7.7). Instead the platform watches
//  what every title ALREADY does and turns it into the same signals:
//    net      a script's net_send(kind, …)        (sending object → its stop)
//    request  a SharedState request kind          (the table's object → its stop)
//    state    a SharedState key changing          (table + key + JSON, prev JSON)
//    badge    ProfileAPI award                    (the GameArea the player stands in)
//    storage  storage.game max/increment/set      (key + new value; the player's GameArea)
//    zone     the local player enters a stop      (its GameArea)
//    enemy    an EasyEnemy dies                   (object → its stop)
//    inactive an object under a stop root goes away (destroyed / SetActive(false))
//  plus marker/name watchers (collectibles, boss, exit; PlaylistAutoSignals).
//
//  Rules are DATA: Generic (any title, name vocabulary only) + per-title
//  recipes keyed by content id — written on the PLATFORM side from each
//  title's existing events (survey Oct 18), so the title ships nothing.
//  A title that references dp.playlist anywhere is OPTED IN: its own signals
//  win and no rule fires for its stops.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using Defective.JSON;

namespace DreamPark
{
    /// One detection rule: "when this source says this, that stop got this signal".
    public sealed class PlaylistAutoRule
    {
        public string source;   // net | request | state | badge | storage | zone | enemy | inactive
        public string name;     // net/request kind · state table · badge id · storage key · zone/inactive path; '*' globs
        public string key;      // state: the key ('*' globs); null = any
        public string kind;     // a PlaylistRunRules signal (reach_exit, defeat_boss, collect, score, complete, failed, started)
                                // or "result": the stop's challenge is over with score n (report_result)
        public string[] when;   // all must hold: "f==v", "f!=v", "f>=n", "f<=n", "prev.f==v", "f?" (present), "prev.f!=v"
        public string n;        // "" none · "count" distinct matches · "sum:f" (CSV or array) · "field:f" · "value" (storage)
        public string distinct; // count: field added to the key that makes a match distinct (e.g. a respawn generation)
        public string at;       // "" the emitter's stop · "target" the leg's target stop · an attraction leaf (glob)
                                // · "@new:f" every stop whose attraction leaf newly appears in field f (a Game Manager's list)
        public string roles;    // CSV of stop roles the rule applies to ("" any)
        public string objectives; // CSV of leg objective types ("" any)
        public string note;     // why (survey evidence) — shown in logs

        public override string ToString() => $"{source}:{name}{(key != null ? "." + key : "")}→{kind}";
    }

    public static class PlaylistAutoRules
    {
        public const string Result = "result";
        public const string Net = "net", Request = "request", State = "state", Badge = "badge", Storage = "storage",
            Zone = "zone", Enemy = "enemy", Inactive = "inactive";

        static PlaylistAutoRule R(string source, string name, string kind, string key = null, string[] when = null,
            string n = "", string at = "", string roles = "", string objectives = "", string distinct = null, string note = null) =>
            new PlaylistAutoRule { source = source, name = name, key = key, kind = kind, when = when ?? Array.Empty<string>(),
                n = n ?? "", at = at ?? "", roles = roles ?? "", objectives = objectives ?? "", distinct = distinct, note = note };

        // ── Generic: any title, names only ─────────────────────────────
        // The words titles already use for their own end events (the runner's own aliases plus common
        // synonyms). A net message / request with one of these names from inside a stop IS that signal.
        public static readonly string[] EndWords =
        {
            "stage_won", "exit_reached", "level_complete", "level_cleared", "level_won", "stage_clear", "stage_cleared",
            "boss_defeated", "boss_killed", "boss_dead", "boss_down", "victory",
        };

        static string EndKind(string w) => w.StartsWith("boss", StringComparison.Ordinal) || w == "victory"
            ? PlaylistRunRules.DefeatBoss : PlaylistRunRules.ReachExit;

        public static readonly List<PlaylistAutoRule> Generic = BuildGeneric();

        static List<PlaylistAutoRule> BuildGeneric()
        {
            var l = new List<PlaylistAutoRule>();
            foreach (var w in EndWords)
            {
                l.Add(R(Net, w, EndKind(w), note: "generic end word"));
                l.Add(R(Request, w, EndKind(w), note: "generic end word"));
            }
            // A badge whose id says "boss", earned while standing in a boss stop.
            l.Add(R(Badge, "*boss*", PlaylistRunRules.DefeatBoss, roles: "boss,miniboss", note: "boss badge in a boss stop"));
            // A score-like storage write while standing in a score stop: the new value is the stop's score.
            l.Add(R(Storage, "*score*", PlaylistRunRules.Score, n: "value", objectives: "score", note: "score key"));
            // An EasyEnemy flagged boss (marker / name) dying inside its stop.
            l.Add(R(Enemy, "boss", PlaylistRunRules.DefeatBoss, note: "EasyEnemy boss died"));
            return l;
        }

        // ── Per-title recipes (platform data; the title ships nothing) ──
        // Evidence: core Tools/Dreamie/playlists-draft/<Title>.md gaps + the Oct 18 auto-objectives survey.
        static readonly Dictionary<string, List<PlaylistAutoRule>> Titles = new Dictionary<string, List<PlaylistAutoRule>>(StringComparer.Ordinal)
        {
            ["BirthdayBash"] = new List<PlaylistAutoRule>
            {
                // bb_boss.lua: dp.shared_state('boss').ph = {p}: 3 defeated → 4 rebaked = the victory is over
                // (2→4 is an abandoned fight, so the previous phase tells them apart).
                R(State, "boss", PlaylistRunRules.DefeatBoss, key: "ph", when: new[] { "p==4", "prev.p==3" },
                    note: "bb_boss ph 3→4"),
                R(Badge, "bb_cake_crusher", PlaylistRunRules.DefeatBoss, note: "awarded at the defeat (fighters only)"),
            },
            ["Critters"] = new List<PlaylistAutoRule>
            {
                // cr_wild: leader writes wild.c<slot> = {st='caught', g=<gen>}; one distinct (slot, gen) = one catch.
                R(State, "wild", PlaylistRunRules.Collect, key: "c*", when: new[] { "st==caught" }, n: "count", distinct: "g",
                    note: "cr_wild catch"),
                // cr_battle: cb.b_<id> ends ph='over' with r='ko' (a knockout, not a forfeit / leave).
                R(State, "cb", PlaylistRunRules.DefeatBoss, key: "b_*", when: new[] { "ph==over", "r==ko" },
                    note: "cr_battle KO"),
                // Camp: arriving is the objective.
                R(Zone, "A_CR_Camp*", PlaylistRunRules.ReachExit, objectives: "reach_exit", note: "arrived at Camp"),
            },
            ["Zombiez"] = new List<PlaylistAutoRule>
            {
                // zombiez_level: boss_killed → send('zb_dead') — only sent with peers (solo has no trace).
                R(Net, "zb_dead", PlaylistRunRules.DefeatBoss, note: "zombiez_level boss died (peers only)"),
                // zombiez_waves: the level's 'zw' record reaches ph='over' (Sequence loop; inside the level root).
                R(State, "zw", PlaylistRunRules.ReachExit, key: "*", when: new[] { "ph==over" }, roles: "start,stage,end",
                    note: "zombiez_waves level over"),
                // Home Free: the team is back at the Survivor Hideout.
                R(Zone, "A_Z_Hideout*", PlaylistRunRules.ReachExit, note: "arrived at the Hideout"),
                // Final Escape: boarding the helicopter.
                R(Net, "zh_board", PlaylistRunRules.ReachExit, note: "zombiez_helipad boarded"),
                // Get to the Chopper!: zadv.p.f = 'a,b,c,d' bin fills (Game Manager) → the helipad stop's count.
                R(State, "zadv", PlaylistRunRules.Collect, key: "p", n: "sum:f", at: "A_Z_*Helipad*", note: "repair bins"),
            },
            ["SuperAdventureLand"] = new List<PlaylistAutoRule>
            {
                // (SAL is opted in — § 7.7 — so these stay idle; they are what zero-change SAL would use.)
                // sal-core boss encounter: dp.shared_state('boss:<fnv>', attraction root).won<epoch> = 1.
                R(State, "boss:*", PlaylistRunRules.DefeatBoss, key: "won*", note: "sal boss encounter won"),
                // victory.lua → sal_badges: dp.profile.awardBadge('sal_<boss>') on every win (once per session).
                R(Badge, "sal_*", PlaylistRunRules.DefeatBoss, roles: "boss,miniboss", note: "sal boss badge"),
            },
            ["ArcadeLand"] = new List<PlaylistAutoRule>
            {
                // Every cabinet's manager: net_send('rd', {t0, …}) = a round is live (sent solo too) → the stop's clock.
                R(Net, "rd", PlaylistRunRules.SignalStarted, note: "arcade round live"),
                // arcade_scoreboard: storage.max('high_score', n) ~2.5 s after a round ends (n > 0) = this round's score.
                R(Storage, "high_score", Result, n: "value", objectives: "time,score", note: "arcade round score"),
            },
            ["DreamSports"] = new List<PlaylistAutoRule>
            {
                // Each sport manager (inside its attraction) writes its bests when the match reaches `results`:
                // tennis best_rally_<V>, bowling best_strikes, golf best_<n> (a min), basketball best_shootout_<V>.
                R(Storage, "best_*", PlaylistRunRules.SignalComplete, note: "match results"),
                // …and wins_<V> / wins when the player beat the CPU — the "champion" stops.
                R(Storage, "wins*", PlaylistRunRules.DefeatBoss, roles: "boss,miniboss", note: "beat the CPU"),
                R(Storage, "plays*", PlaylistRunRules.SignalStarted, note: "match started"),
                R(Zone, "A_Plaza_Gate*", PlaylistRunRules.ReachExit, objectives: "reach_exit", note: "through the gate"),
            },
            ["CrashCourse"] = new List<PlaylistAutoRule>
            {
                // cc_badges: every finished race increments bd_races (game scope; the track is the player's GameArea).
                R(Storage, "bd_races", PlaylistRunRules.ReachExit, note: "race finished"),
            },
            ["WizardsWay"] = new List<PlaylistAutoRule>
            {
                // combat-director: cb.s.ph = 'won' (bosses AND battle stops; complete counts for either objective).
                R(State, "cb", PlaylistRunRules.SignalComplete, key: "s", when: new[] { "ph==won" }, note: "combat won"),
                // Wand Duel: wd.s.ph = 'over' (leader; 2 players).
                R(State, "wd", PlaylistRunRules.SignalComplete, key: "s", when: new[] { "ph==over" }, note: "duel over"),
                // Game Manager: ww_game.run.ad lists completed levels ('epoch|A_…'): a stop newly in it is complete.
                R(State, "ww_game", PlaylistRunRules.SignalComplete, key: "run", at: "@new:ad", note: "level completed"),
                // Quest openings: walking into the arrival stop.
                R(Zone, "*Arrival*", PlaylistRunRules.ReachExit, objectives: "reach_exit", note: "arrived"),
            },
            ["LaserTag"] = new List<PlaylistAutoRule>
            {
                // lasertag_boss die(): the boss's Visual child switches off (no event solo).
                R(Inactive, "P_LT_Boss_*/Visual", PlaylistRunRules.DefeatBoss, note: "boss Visual off"),
                // PvP: game-container pvp_finish → lt_pvp.x = {v, at, why} (time / goal / alone / nobody).
                R(State, "lt_pvp", PlaylistRunRules.SignalComplete, key: "x", when: new[] { "why?" }, at: "target",
                    objectives: "score", note: "pvp_finish"),
            },
        };

        public static IReadOnlyList<PlaylistAutoRule> ForTitle(string contentId) =>
            contentId != null && Titles.TryGetValue(contentId, out var l) ? l : (IReadOnlyList<PlaylistAutoRule>)Array.Empty<PlaylistAutoRule>();

        public static IEnumerable<string> TitlesWithRecipes => Titles.Keys;

        /// Generic rules then the title's recipe.
        public static IEnumerable<PlaylistAutoRule> RulesFor(string contentId)
        {
            foreach (var r in Generic) yield return r;
            foreach (var r in ForTitle(contentId)) yield return r;
        }

        // ── Opt-in ───────────────────────────────────────────────────
        /// A title that mentions dp.playlist in any script has opted in: its own signals win.
        public static bool OptedIn(string luaSource) =>
            !string.IsNullOrEmpty(luaSource) && luaSource.IndexOf("dp.playlist", StringComparison.Ordinal) >= 0;

        // ── Matching ─────────────────────────────────────────────────
        /// '*' matches any run of characters; case-insensitive.
        public static bool Glob(string pattern, string s)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
            if (s == null) return false;
            return GlobAt(pattern.ToLowerInvariant(), 0, s.ToLowerInvariant(), 0);
        }

        static bool GlobAt(string p, int pi, string s, int si)
        {
            while (pi < p.Length)
            {
                if (p[pi] == '*')
                {
                    while (pi < p.Length && p[pi] == '*') pi++;
                    if (pi == p.Length) return true;
                    for (int k = si; k <= s.Length; k++) if (GlobAt(p, pi, s, k)) return true;
                    return false;
                }
                if (si >= s.Length || p[pi] != s[si]) return false;
                pi++; si++;
            }
            return si == s.Length;
        }

        public static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        static bool InCsv(string csv, string v)
        {
            if (string.IsNullOrEmpty(csv)) return true;
            if (string.IsNullOrEmpty(v)) return false;
            foreach (var part in csv.Split(','))
                if (string.Equals(part.Trim(), v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// Does the rule apply to this stop of this leg (role / objective filters)?
        public static bool Applies(PlaylistAutoRule r, PlaylistRunLeg leg, PlaylistRunStop stop) =>
            r != null && stop != null && InCsv(r.roles, stop.role) && InCsv(r.objectives, leg?.objective?.type);

        /// Is the event's name/key the rule's?
        public static bool Hits(PlaylistAutoRule r, string source, string name, string key) =>
            r != null && r.source == source && Glob(r.name, name) && (r.key == null || Glob(r.key, key));

        /// A dotted field of a JSON value (null when absent).
        public static JSONObject Field(JSONObject j, string path)
        {
            if (j == null || string.IsNullOrEmpty(path)) return j;
            JSONObject cur = j;
            foreach (var part in path.Split('.'))
            {
                if (cur == null || !cur.isContainer) return null;
                cur = cur.GetField(part);
            }
            return cur;
        }

        public static string Text(JSONObject v)
        {
            if (v == null || v.type == JSONObject.Type.Null) return null;
            switch (v.type)
            {
                case JSONObject.Type.String: return v.stringValue;
                case JSONObject.Type.Bool: return v.boolValue ? "true" : "false";
                case JSONObject.Type.Number: return v.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                default: return v.Print();
            }
        }

        static double Num(JSONObject v)
        {
            if (v == null) return double.NaN;
            if (v.type == JSONObject.Type.Number) return v.doubleValue;
            return double.TryParse(Text(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }

        /// Every `when` condition holds for the new value (and the previous one, for prev.*).
        public static bool When(PlaylistAutoRule r, JSONObject cur, JSONObject prev)
        {
            if (r?.when == null) return true;
            foreach (var raw in r.when)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string c = raw.Trim();
                JSONObject src = cur;
                if (c.StartsWith("prev.", StringComparison.Ordinal)) { src = prev; c = c.Substring(5); }
                if (c.EndsWith("?", StringComparison.Ordinal))
                {
                    if (Field(src, c.Substring(0, c.Length - 1)) == null) return false;
                    continue;
                }
                string op = c.Contains("==") ? "==" : c.Contains("!=") ? "!=" : c.Contains(">=") ? ">=" : c.Contains("<=") ? "<=" : null;
                if (op == null) return false;
                int at = c.IndexOf(op, StringComparison.Ordinal);
                string f = c.Substring(0, at).Trim(), want = c.Substring(at + 2).Trim();
                var v = Field(src, f);
                switch (op)
                {
                    case "==": if (!Same(v, want)) return false; break;
                    case "!=": if (Same(v, want)) return false; break;
                    case ">=": { double d = Num(v); if (double.IsNaN(d) || d < Parse(want)) return false; break; }
                    case "<=": { double d = Num(v); if (double.IsNaN(d) || d > Parse(want)) return false; break; }
                }
            }
            return true;
        }

        static double Parse(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

        static bool Same(JSONObject v, string want)
        {
            string t = Text(v);
            if (t == null) return false;
            if (t == want) return true;
            double a = Num(v), b = Parse(want);
            return !double.IsNaN(a) && !double.IsNaN(b) && Math.Abs(a - b) < 1e-9;
        }

        /// The rule's progress value from the event (null = none). `count` is the caller's distinct tally.
        public static double? N(PlaylistAutoRule r, JSONObject cur, double? value, int count)
        {
            string n = r?.n ?? "";
            if (n.Length == 0) return null;
            if (n == "count") return count;
            if (n == "value") return value;
            if (n.StartsWith("field:", StringComparison.Ordinal))
            {
                double d = Num(Field(cur, n.Substring(6)));
                return double.IsNaN(d) ? (double?)null : d;
            }
            if (n.StartsWith("sum:", StringComparison.Ordinal))
            {
                var f = Field(cur, n.Substring(4));
                if (f == null) return null;
                double sum = 0;
                if (f.isContainer) { foreach (var e in f.list) { double d = Num(e); if (!double.IsNaN(d)) sum += d; } }
                else
                    foreach (var part in (Text(f) ?? "").Split(','))
                    {
                        double d = Parse(part.Trim());
                        if (!double.IsNaN(d)) sum += d;
                    }
                return sum;
            }
            return null;
        }

        /// The identity a `count` rule tallies once: the key plus its distinct field.
        public static string DistinctKey(PlaylistAutoRule r, string key, JSONObject cur) =>
            (key ?? "") + "|" + (string.IsNullOrEmpty(r?.distinct) ? "" : Text(Field(cur, r.distinct)) ?? "");

        /// Stops a "@new:f" rule names: attraction leaves that appear in cur.f's text but not in prev.f's.
        public static List<PlaylistRunStop> NewlyNamed(string at, PlaylistRunLeg leg, JSONObject cur, JSONObject prev)
        {
            var l = new List<PlaylistRunStop>();
            if (leg == null || at == null || !at.StartsWith("@new:", StringComparison.Ordinal)) return l;
            string f = at.Substring(5);
            string now = Text(Field(cur, f)) ?? "", before = Text(Field(prev, f)) ?? "";
            foreach (var s in leg.stops)
            {
                string leaf = Leaf(s?.attraction);
                if (leaf.Length == 0) continue;
                if (now.IndexOf(leaf, StringComparison.Ordinal) >= 0 && before.IndexOf(leaf, StringComparison.Ordinal) < 0) l.Add(s);
            }
            return l;
        }

        /// One-shot kinds fire once per stop; progress kinds fire when n grows.
        public static bool IsProgress(string kind) => kind == PlaylistRunRules.Collect || kind == PlaylistRunRules.Score;

        // ── Name conventions (no marker needed) ───────────────────────
        // Only consulted for a stop with NO PlaylistObjectiveMarker, and only for the matching objective.
        /// A coin by tag, or a name with a COIN word ("Coin", "E_COIN (3)", "E_COIN(Clone)") that is not a red /
        /// block / hunt variant (SAL: red coins don't count, block coins sit inside unhit blocks, hunt coins hide).
        public static bool LooksCollectible(string name, string tag)
        {
            if (Is(tag, "Coin") || Is(tag, "Collectible") || Starts(name, "collectible")) return true;
            bool coin = false;
            foreach (var w in Words(name))
            {
                if (w == "red" || w == "block" || w == "iceblock" || w == "coinhunt" || w == "hunt" || w == "spawner") return false;
                if (w == "coin") coin = true;
            }
            return coin;
        }

        static IEnumerable<string> Words(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            foreach (var w in name.ToLowerInvariant().Split(new[] { '_', ' ', '(', ')', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
                yield return w;
        }

        public static bool LooksBoss(string name, string tag) => Is(tag, "Boss") || Starts(name, "boss") || Contains(name, "_boss");

        public static bool LooksExit(string name, string tag) =>
            Is(tag, "Exit") || Is(tag, "Finish") || Starts(name, "exit") || Starts(name, "finishline") || Starts(name, "finish_line")
            || Starts(name, "goalflag") || Starts(name, "goal_flag");

        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static bool Starts(string s, string p) => s != null && s.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        static bool Contains(string s, string p) => s != null && s.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
