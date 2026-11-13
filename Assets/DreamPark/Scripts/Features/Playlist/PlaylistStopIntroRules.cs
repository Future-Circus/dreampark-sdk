// ─────────────────────────────────────────────────────────────────────
//  MOVING TO SAL; NOT PLATFORM (PLAYLIST-PLAN § 6.1.5 / § 6.3.5, Oct 21 2026): games own their instruction UI. Kept until
//  SuperAdventureLand ports this card code into SAL; the platform shows no objective cards once pl-sets switches
//  off the PlaylistObjectiveCallout call sites. Don't add features here.
//  PlaylistStopIntroRules.cs — the STOP INTRO (Docs/PLAYLISTS.md § 7.4).
//  Aidan: "I just want to make sure the user knows what to do at each
//  attraction, always" — and: players enter at different times and wander
//  off to other attractions, in ONE shared physical space, so an attraction
//  someone is already playing can never be frozen.
//
//  PERSONAL by default: when a player walks into a stop, a goal card appears
//  in front of THAT player (body-locked), Dreamie says it in their ear and
//  points from their own DreamBand, and a short "You're in — GO!" beat. No
//  pause, never synced.
//  The only hold: an IDLE stop (nobody has started it) may hold ≤ 3 s for the
//  FIRST arrival's intro + 3·2·1; anyone arriving inside that window shares it.
//  Once a stop is live it never holds again.
//  A quick TIMED stop (a ≤ 90 s mini-game round) is the exception: the round
//  starts with the first player after a short "Join now!" window (≤ 5 s).
//  Variants: FULL (first time this kind of stop), SHORT (seen this run),
//  BOSS (a reveal: "BOSS: King Jubbo" + the title's hint), TIMED.
//  The leg start ring and the Results Room stay the only group-wide moments.
// ─────────────────────────────────────────────────────────────────────

using System;

namespace DreamPark
{
    public static class PlaylistStopIntroRules
    {
        public const string Full = "full", Short = "short", Boss = "boss", Timed = "timed";
        public const string PhaseCard = "card", PhaseCountdown = "countdown", PhaseGo = "go", PhaseDone = "done";

        /// The idle-stop hold (≤ 3 s): card, then 3·2·1 (0.6 s each), then GO.
        public const double IdleHoldSeconds = 3.0;
        /// A timed mini-game round: "Join now!" then the same 3·2·1 (≤ 5 s).
        public const double TimedHoldSeconds = 5.0;
        public const double CountStepSeconds = 0.6, GoSeconds = 0.3;
        /// A stop this short (its timer) is a timed round.
        public const double TimedStopSeconds = 90.0;
        /// How long a PERSONAL card stays (no hold anywhere).
        public const double PersonalFull = 3.5, PersonalShort = 1.8, PersonalBoss = 4.5, PersonalTimed = 2.5;
        /// After the shared GO the card lingers this long for reading, then fades (nothing waits for it).
        public const double LingerSeconds = 1.5;
        /// No progress at a stop for this long while a player is in it → Dreamie re-explains, the card replays.
        public const double StuckSeconds = 60.0;
        /// The same player's "what do I do?" replay (a wrist glance) at most this often.
        public const double ReplayCooldownSeconds = 20.0;
        /// A device that asked for the idle intro and heard nothing for this long shows its personal card.
        public const double AskTimeoutSeconds = 1.0;

        public static bool IsBoss(PlaylistRunStop s) => s != null && (s.role == "boss" || s.role == "miniboss");

        /// Which intro this stop gets: a timed round, a boss reveal, the short one (this kind seen this run), else full.
        public static string Variant(PlaylistRunStop stop, double stopSeconds, bool seenKindBefore)
        {
            if (stopSeconds > 0 && stopSeconds <= TimedStopSeconds && !IsBoss(stop)) return Timed;
            if (IsBoss(stop)) return Boss;
            return seenKindBefore ? Short : Full;
        }

        /// The idle-stop hold for a variant (the only time a stop ever waits).
        public static double HoldSeconds(string variant) => variant == Timed ? TimedHoldSeconds : IdleHoldSeconds;

        static double CountdownTotal => 3 * CountStepSeconds;
        public static double CardSeconds(string variant) => HoldSeconds(variant) - CountdownTotal - GoSeconds;

        /// May this arrival start the stop's (shared) intro? Only an idle stop: no intro yet, nothing played there.
        public static bool CanStartIdleIntro(bool introStarted, bool anyProgress, bool stopOver) =>
            !introStarted && !anyProgress && !stopOver;

        /// Does an arrival at <paramref name="now"/> share the intro that started at <paramref name="introAt"/>?
        public static bool SharesIntro(double introAt, string variant, double now) =>
            introAt > 0 && now < introAt + HoldSeconds(variant);

        /// Is the stop held right now (its idle intro is running)?
        public static bool Held(double introAt, string variant, double now) =>
            introAt > 0 && now >= introAt && now < introAt + HoldSeconds(variant);

        /// Where a shared intro is, `t` seconds after it started: the phase and the count (3, 2, 1) or 0.
        public static string PhaseAt(string variant, double t, out int count)
        {
            count = 0;
            double card = CardSeconds(variant);
            if (t < card) return PhaseCard;
            double c = t - card;
            if (c < CountdownTotal) { count = 3 - (int)Math.Floor(c / CountStepSeconds); return PhaseCountdown; }
            if (c < CountdownTotal + GoSeconds) return PhaseGo;
            return t < HoldSeconds(variant) + LingerSeconds ? PhaseGo : PhaseDone;
        }

        /// How long a personal card stays.
        public static double PersonalSeconds(string variant) =>
            variant == Boss ? PersonalBoss : variant == Short ? PersonalShort : variant == Timed ? PersonalTimed : PersonalFull;

        /// The card's headline.
        public static string Headline(string variant, PlaylistRunStop s, bool sharedIdle)
        {
            if (variant == Boss) return "BOSS: " + PlaylistObjectiveText.Name(s);
            if (variant == Timed && sharedIdle) return "Join now! " + PlaylistObjectiveText.Name(s);
            return PlaylistObjectiveText.Name(s);
        }

        /// The personal beat under the goal: "You're in — GO!" first, then the team's shared progress.
        public static string PersonalFooter(double secondsShown, PlaylistStopProgress p, string unit)
        {
            if (secondsShown < 1.4) return "You're in — GO!";
            return PlaylistObjectiveText.Team(p, unit) ?? "You're in — GO!";
        }

        /// Stuck: a player is in the stop, it is not over, and nothing has changed for StuckSeconds — but never in a live
        /// timed round: a stop whose title signalled `started` with time still on its clock (<paramref name="secondsLeft"/>
        /// = <see cref="PlaylistRunRules.StopLeft"/>, -1 = untimed / not started / over) is being played, progress or not.
        public static bool IsStuck(bool playerInside, bool over, double now, double lastChange, bool started = false, double secondsLeft = -1)
            => playerInside && !over && !(started && secondsLeft > 0) && lastChange > 0 && now - lastChange >= StuckSeconds;
    }
}
