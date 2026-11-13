// ─────────────────────────────────────────────────────────────────────
//  MOVING TO SAL; NOT PLATFORM (PLAYLIST-PLAN § 6.1.5 / § 6.3.5, Oct 21 2026): games own their instruction UI. Kept until
//  SuperAdventureLand ports this card code into SAL; the platform shows no objective cards once pl-sets switches
//  off the PlaylistObjectiveCallout call sites. Don't add features here.
//  PlaylistObjectiveText.cs — what players are TOLD (Docs/PLAYLISTS.md § 7.4).
//  Aidan: "how do players actually know what the objective is in each?"
//  One pure phrasing rule turns (leg objective, stop role, the title's own
//  progress report) into:
//    • the GOAL line: "Collect all 7 coins!", "Defeat King Jubbo!",
//      "Reach the exit!", "Score 600 in Meteors!" — the headset's callout
//      card, Dreamie's line, the phone;
//    • the CHIP: "Coins 3/7", "King Jubbo", "1:42" — the DreamBand face.
//  Authors override with `objectiveText` on the stop or the leg.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DreamPark
{
    public static class PlaylistObjectiveText
    {
        /// "A_KingJubbo" → "King Jubbo", "A_LT_RW_Lobby" → "LT RW Lobby"; a displayName wins.
        public static string Name(PlaylistRunStop s)
        {
            if (s == null) return "";
            if (!string.IsNullOrEmpty(s.displayName)) return s.displayName;
            string a = s.attraction ?? "";
            int slash = a.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) a = a.Substring(slash + 1);
            if (a.StartsWith("A_", StringComparison.Ordinal) || a.StartsWith("P_", StringComparison.Ordinal)) a = a.Substring(2);
            var sb = new StringBuilder(a.Length + 8);
            for (int i = 0; i < a.Length; i++)
            {
                char c = a[i];
                if (c == '_') { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                bool boundary = i > 0 && char.IsUpper(c) && (char.IsLower(a[i - 1]) || (i + 1 < a.Length && char.IsLower(a[i + 1]) && char.IsUpper(a[i - 1])));
                if (boundary && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        static string Unit(string unit, string kind)
        {
            if (!string.IsNullOrEmpty(unit)) return unit;
            return kind == PlaylistRunRules.Score ? "points" : "treasures";
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        static string N(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

        static bool IsBoss(PlaylistRunStop s) => s != null && (s.role == "boss" || s.role == "miniboss");

        /// The stop's one-line goal, said when it goes live (and while it plays).
        public static string StopGoal(PlaylistRunLeg leg, PlaylistRunStop s, PlaylistStopProgress p)
        {
            if (s == null) return LegGoal(leg);
            if (!string.IsNullOrEmpty(s.objectiveText)) return Authored(leg, s, p);
            return WithBonus(BaseGoal(leg, s, p), s, leg);
        }

        /// The stop's AUTHORED goal line (Oct 20, Meadow: the cards read "Defeat all 3 Jubbos in Jubbo Gang!" instead of
        /// "Kick all 3 Jubbos away from the ! block!"). Its count must equal the live progress `of`: one number in it that
        /// differs is replaced by the live one; several (ambiguous) → the template with the live number, in the line's
        /// own verb ("Kick all 5 Jubbos in Jubbo Gang!").
        static string Authored(PlaylistRunLeg leg, PlaylistRunStop s, PlaylistStopProgress p)
        {
            string text = s.objectiveText;
            if (p == null || p.goal <= 0) return text;
            var nums = System.Text.RegularExpressions.Regex.Matches(text, @"(?<![\w.])\d+(?![\w.])");
            if (nums.Count == 0) return text;
            string live = N(p.goal);
            bool anyMatches = false;
            foreach (System.Text.RegularExpressions.Match m in nums) if (m.Value == live) anyMatches = true;
            if (anyMatches) return text;
            if (nums.Count == 1) return text.Substring(0, nums[0].Index) + live + text.Substring(nums[0].Index + nums[0].Length);
            string verb = FirstWord(text);
            string k = KindGoal(PlaylistChallenge.KindOf(leg, s), leg, s, p, verb);
            return WithBonus(k ?? BaseGoal(leg, s, p), s, leg);
        }

        static string FirstWord(string text)
        {
            int i = 0;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            return i > 1 ? text.Substring(0, i) : null;
        }

        // The catalog's goal templates (§ 12) for the kinds the leg objective cannot phrase on its own.
        static string KindGoal(string kind, PlaylistRunLeg leg, PlaylistRunStop s, PlaylistStopProgress p, string verb = null)
        {
            var ob = leg?.objective ?? new PlaylistRunObjective();
            string name = Name(s);
            bool has = p != null && p.goal > 0;
            double count = has ? p.goal : ob.count;
            string unit = !string.IsNullOrEmpty(p?.unit) ? p.unit : null;
            switch (kind)
            {
                case PlaylistChallenge.Clear: return has && unit != null ? $"{verb ?? "Defeat"} all {N(count)} {unit} in {name}!" : $"Clear {name}!";
                case PlaylistChallenge.Race: return $"Win the race at {name}!";
                case PlaylistChallenge.Versus: return count > 0 ? $"First to {N(count)} wins!" : $"Win the match at {name}!";
                case PlaylistChallenge.Catch: return count > 0 ? $"Catch {N(count)} {unit ?? "critters"}!" : $"Catch a critter in {name}!";
                case PlaylistChallenge.Find: return count > 0 ? $"Find the {N(count)} hidden {unit ?? "secrets"}!" : $"Find the secret in {name}!";
                case PlaylistChallenge.Solve: return $"Solve {name}!";
                case PlaylistChallenge.Protect: return has ? $"Bring all {N(count)} {unit ?? "treasures"} home!" : $"Keep {name} safe!";
                case PlaylistChallenge.Moment: return $"Enjoy {name}!";
            }
            return null;
        }

        /// "...without getting hit!" / "...and chain a combo!" / "...as fast as you can!" on the goal line.
        static string WithBonus(string goal, PlaylistRunStop s, PlaylistRunLeg leg)
        {
            if (s?.bonus == null || s.bonus.Count == 0 || string.IsNullOrEmpty(goal)) return goal;
            string g = goal.TrimEnd('!');
            foreach (var b in s.bonus)
            {
                if (b == PlaylistRunRules.BonusNoHit) g += " without getting hit";
                else if (b == PlaylistRunRules.BonusCombo) g += " and chain a combo";
                else if (b == PlaylistRunRules.BonusClock)
                {
                    double par = PlaylistRunRules.StopTimer(null, s);
                    g += par > 0 ? $" in under {N(par)} seconds" : " as fast as you can";
                }
            }
            return g + "!";
        }

        static string BaseGoal(PlaylistRunLeg leg, PlaylistRunStop s, PlaylistStopProgress p)
        {
            string kind = PlaylistChallenge.KindOf(leg, s);
            if (!string.IsNullOrEmpty(s.challenge) && kind != PlaylistChallenge.Collect && kind != PlaylistChallenge.ReachEnd
                && kind != PlaylistChallenge.Boss && kind != PlaylistChallenge.Score && kind != PlaylistChallenge.Survive)
            {
                string k = KindGoal(kind, leg, s, p);
                if (k != null) return k;
            }
            var ob = leg?.objective ?? new PlaylistRunObjective();
            var target = PlaylistRunRules.TargetStop(leg);
            string name = Name(s);
            if (IsBoss(s)) return $"Defeat {name}!";
            if (p != null && p.goal > 0)
            {
                string unit = Unit(p.unit, ob.type);
                return unit == "points" ? $"Score {N(p.goal)} in {name}!" : $"Collect all {N(p.goal)} {unit}!";
            }
            switch (ob.type)
            {
                case PlaylistRunRules.Score:
                    return ob.count > 0 ? $"Score {N(ob.count)} in {name}!" : $"Score big in {name}!";
                case PlaylistRunRules.Collect:
                    return ob.count > 0 && (target == null || target == s) ? $"Collect {N(ob.count)} {Unit(null, ob.type)}!" : $"Collect everything in {name}!";
                case PlaylistRunRules.Survive:
                    return ob.seconds > 0 ? $"Survive {N(ob.seconds)} seconds!" : "Survive!";
                case PlaylistRunRules.Time:
                    return ob.seconds > 0 ? $"{name}: {N(ob.seconds)} seconds — go!" : $"Enjoy {name}!";
                case PlaylistRunRules.DefeatBoss:
                    return target != null && target != s ? $"Clear {name}, then take on {Name(target)}!" : $"Clear {name}!";
                default:
                    return s.role == "end" || target == s ? $"Reach the end of {name}!" : $"Make it through {name}!";
            }
        }

        /// The leg's one-line goal (the queue item's headline).
        public static string LegGoal(PlaylistRunLeg leg)
        {
            if (leg == null) return "";
            if (!string.IsNullOrEmpty(leg.objectiveText)) return leg.objectiveText;
            var ob = leg.objective ?? new PlaylistRunObjective();
            var t = PlaylistRunRules.TargetStop(leg);
            switch (ob.type)
            {
                case PlaylistRunRules.DefeatBoss: return t != null ? $"Defeat {Name(t)}!" : "Defeat the boss!";
                case PlaylistRunRules.Score: return ob.count > 0 ? (t != null ? $"Score {N(ob.count)} in {Name(t)}!" : $"Score {N(ob.count)}!") : "Score big!";
                case PlaylistRunRules.Collect: return ob.count > 0 ? $"Collect {N(ob.count)} treasures!" : "Collect everything!";
                case PlaylistRunRules.Survive: return ob.seconds > 0 ? $"Survive {N(ob.seconds)} seconds!" : "Survive!";
                case PlaylistRunRules.Time: return ob.seconds > 0 ? $"{N(ob.seconds)} seconds — go!" : "Go!";
                default: return t != null ? $"Reach the end of {Name(t)}!" : "Reach the exit!";
            }
        }

        static string Clock(double seconds)
        {
            int s = (int)Math.Ceiling(Math.Max(0, seconds));
            return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// The wrist chip for a stop: "Coins 3/7", "King Jubbo", or the stop timer "1:42" in its last minute.
        public static string Chip(PlaylistRunLeg leg, PlaylistRunStop s, PlaylistStopProgress p, double secondsLeft,
            double legProgress = 0, double legGoal = 0)
        {
            if (s == null) return "";
            if (secondsLeft >= 0 && secondsLeft <= 60) return Clock(secondsLeft);
            if (IsBoss(s)) return Name(s);
            var ob = leg?.objective ?? new PlaylistRunObjective();
            string kind = PlaylistChallenge.KindOf(leg, s);
            if (p != null && p.goal > 0)
            {
                string nn = N(Math.Min(p.n, p.goal)), of = N(p.goal);
                switch (kind)
                {
                    case PlaylistChallenge.Catch: return $"Caught {nn}/{of}";
                    case PlaylistChallenge.Find: return $"Found {nn}/{of}";
                    case PlaylistChallenge.Race: return $"Lap {nn}/{of}";
                    case PlaylistChallenge.Versus: return $"{nn}-{of}";
                }
                return $"{Cap(Unit(p.unit, ob.type))} {nn}/{of}";
            }
            if ((ob.type == PlaylistRunRules.Score || ob.type == PlaylistRunRules.Collect) && ob.count > 0)
                return $"{Cap(Unit(p?.unit, ob.type))} {N(Math.Min(legProgress, legGoal > 0 ? legGoal : ob.count))}/{N(legGoal > 0 ? legGoal : ob.count)}";
            if (secondsLeft >= 0) return Clock(secondsLeft);
            return Name(s);
        }

        /// Shared progress, said on the personal card: "Your team: 3/7 coins" (null when the stop has no count).
        public static string Team(PlaylistStopProgress p, string kind)
        {
            if (p == null || p.goal <= 0) return null;
            return $"Your team: {N(Math.Min(p.n, p.goal))}/{N(p.goal)} {Unit(p.unit, kind)}";
        }

        /// A boss that has held out for <see cref="PlaylistRunRules.BossHintSeconds"/>: a gentle nudge (Dreamie
        /// gets a `boss_hint` cue to say something better).
        public static string BossHint(PlaylistRunStop s) => $"Keep at it — {Name(s)} is almost beaten!";

        /// The hint line for a stop past its kind's hint time (boss 180 s, solve 120 s).
        public static string Hint(PlaylistRunLeg leg, PlaylistRunStop s) =>
            PlaylistChallenge.KindOf(leg, s) == PlaylistChallenge.Solve
                ? $"Need a hand with {Name(s)}? Look around for clues!" : BossHint(s);
    }
}
