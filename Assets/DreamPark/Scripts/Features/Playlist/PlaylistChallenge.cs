// ─────────────────────────────────────────────────────────────────────
//  PlaylistChallenge.cs — what KIND of challenge a stop is (objective
//  catalog, Docs/PLAYLISTS.md § 12; core Tools/Dreamie/objectives/catalog.json).
//  A stop's `challenge` is authored, else derived (boss/miniboss → boss;
//  the leg objective collect → collect, score → score, survive → survive,
//  time → moment, reach_exit → reach_end). The kind drives the goal line
//  template, the wrist chip, the default soft time limit and the hint time,
//  and keys the analytics. Completion rules are unchanged (§ 7.3).
// ─────────────────────────────────────────────────────────────────────

namespace DreamPark
{
    public static class PlaylistChallenge
    {
        public const string Collect = "collect", ReachEnd = "reach_end", Boss = "boss", Clear = "clear",
                            Survive = "survive", Score = "score", Race = "race", Versus = "versus", Catch = "catch",
                            Find = "find", Solve = "solve", Protect = "protect", Moment = "moment",
                            /// Start line → finish line without failing (stop.crossing: lava, Pengo eggs …; goal-ui Oct 20).
                            Crossing = "crossing",
                            /// Jump pads: step-in circles joined by an arch, pad → pad (stop.jump{pads}; goal-ui Oct 20).
                            Jump = "jump";

        public static readonly string[] Kinds =
            { Collect, ReachEnd, Boss, Clear, Survive, Score, Race, Versus, Catch, Find, Solve, Protect, Moment, Crossing, Jump };

        /// The on-screen name of a kind (Aidan, Oct 20: "Crossing", "Jump", "Reach").
        public static string Label(string kind)
        {
            switch (kind)
            {
                case Crossing: return "Crossing";
                case Jump: return "Jump";
                case ReachEnd: return "Reach";
                case null: case "": return "";
                default: return char.ToUpperInvariant(kind[0]) + kind.Substring(1).Replace('_', ' ');
            }
        }

        public static bool Known(string k) => System.Array.IndexOf(Kinds, k) >= 0;

        /// Stops with no fail condition on the clock: an AUTHORED `moment` (a shop waypoint, a story scene) simply
        /// ends, and a `survive` stop whose time runs out was survived. Their timeout is `done` — never a lost life or
        /// a failure. (A moment DERIVED from a `time` leg is a timed mini-game: a silent timeout still costs one.)
        public static bool TimeoutIsDone(PlaylistRunLeg leg, PlaylistRunStop s) =>
            s != null && (s.challenge == Moment || KindOf(leg, s) == Survive);

        /// An authored challenge id: a catalog kind, or a pool challenge (pl-sets, PLAYLIST-PLAN § 3.2: coins, bad_guys, break, or an
        /// attraction's unique one — crossing, hunt, targets, medal, dig, presents, snowman, thief, puzzle …).
        public static bool Authored(string k)
        {
            if (string.IsNullOrEmpty(k) || k.Length > 40 || !(char.IsLetterOrDigit(k[0]) && k[0] < 128 && !char.IsUpper(k[0]))) return false;
            foreach (char c in k) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            return true;
        }

        /// The stop's kind: authored (any challenge id — its `<Attraction>.<id>` goal block resolves), else derived from its
        /// role and the leg's objective.
        public static string KindOf(PlaylistRunLeg leg, PlaylistRunStop s)
        {
            if (s != null && Authored(s.challenge)) return s.challenge;
            return DerivedKind(leg, s);
        }

        /// The kind a stop has without an authored challenge: boss / miniboss → boss, else the leg objective's.
        public static string DerivedKind(PlaylistRunLeg leg, PlaylistRunStop s)
        {
            if (s != null && (s.role == "boss" || s.role == "miniboss")) return Boss;
            switch (leg?.objective?.type)
            {
                case PlaylistRunRules.Collect: return Collect;
                case PlaylistRunRules.Score: return Score;
                case PlaylistRunRules.Survive: return Survive;
                case PlaylistRunRules.Time: return Moment;
                default: return ReachEnd;
            }
        }

        /// Default soft time limit per kind (catalog kinds[].timeLimit) when nothing more specific is set:
        /// boss never; survive = the goal; catch 300; solve 300; clear / protect 240; find / crossing / jump 180; versus 360;
        /// race 600 (the title's own cap usually applies); moment 60; collect / reach_end / score 210.
        public static double DefaultSeconds(string kind, PlaylistRunLeg leg)
        {
            switch (kind)
            {
                case Boss: return 0;
                case Survive: return leg?.objective?.seconds > 0 ? leg.objective.seconds : PlaylistRunRules.DefaultStopSeconds;
                case Catch: case Solve: return 300;
                case Clear: case Protect: return 240;
                case Find: case Crossing: case Jump: return 180;
                case Versus: return 360;
                case Race: return 600;
                case Moment: return 60;
                default: return PlaylistRunRules.DefaultStopSeconds;
            }
        }

        /// After this long unbeaten, a hint (boss 180 s, solve 120 s), 0 = none.
        /// A boss goal block with no timer of its own: no credit after this long (the safety net's `skip`, CONTRACT § 2).
        public const double BossSkipSeconds = 600.0;

        public static double HintSeconds(string kind) =>
            kind == Boss ? PlaylistRunRules.BossHintSeconds : kind == Solve ? 120.0 : 0.0;
    }
}
