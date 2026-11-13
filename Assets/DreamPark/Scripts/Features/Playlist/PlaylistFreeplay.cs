// ─────────────────────────────────────────────────────────────────────
//  PlaylistFreeplay.cs — EVERY session runs a PlaylistRun (repair-oct20 CONTRACT § 3; Aidan, Oct 20:
//  "games are always in a playlist"). With no playlist chosen, a title's package runs its DEFAULT
//  playlist: the backend's `block.defaultPlaylist`, else the generated `freeplay` record (one open
//  round over everything that stands, open goal blocks, the Dreamie script). Freeplay carries
//  `rules.navigation = "title"`: the central dp.load_level guard lets the title's own level / hub
//  navigation through (authored playlists stay guarded). Pure: the plan is a function of the package's
//  standing occurrences + the record, so every device builds the same one (same SharedState).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;

namespace DreamPark
{
    public static class PlaylistFreeplay
    {
        public const string DefaultId = "freeplay", LegId = "freeplay";
        /// A freeplay round has no clock of its own; it ends when the session moves on (the queue / a playlist pack).
        public const int RoundSeconds = 3600;

        /// The title owns navigation in this plan (freeplay / rules.navigation "title").
        public static bool TitleNavigation(PlaylistRunPlan plan) =>
            plan != null && plan.rules != null && plan.rules.navigation == PlaylistRunPolicy.NavigationTitle;

        /// This plan is a title's free play (no stop intros, holds, cards or soft timers: the title runs as it always does).
        public static bool Is(PlaylistRunPlan plan) => TitleNavigation(plan) && plan.legs != null && plan.legs.Count == 1;

        /// The run of a package's free play (stable on every device: one per package instance).
        public static string RunId(string instanceId) => "freeplay-" + (instanceId ?? "");

        /// The default plan of a standing package: one open round over its occurrences (occurrenceId, attraction leaf,
        /// role) in occurrence-id order, with the record's id, name, Dreamie script, Lua and per-attraction goals when given.
        public static PlaylistRunPlan Plan(string contentId, string instanceId, IEnumerable<(string occurrenceId, string attraction, string role)> standing,
            PackagePlaylist record = null)
        {
            var stops = new List<(string occ, string attraction, string role)>();
            foreach (var s in standing ?? Array.Empty<(string, string, string)>())
                if (!string.IsNullOrEmpty(s.occurrenceId)) stops.Add((s.occurrenceId, Leaf(s.attraction), string.IsNullOrEmpty(s.role) ? "stage" : s.role));
            stops.Sort((a, b) => string.CompareOrdinal(a.occ, b.occ));
            var leg0 = record?.arc != null && record.arc.Count > 0 ? record.arc[0] : null;
            var plan = new PlaylistRunPlan
            {
                playlistId = !string.IsNullOrEmpty(record?.id) ? record.id : DefaultId,
                contentId = contentId, instanceId = instanceId,
                name = !string.IsNullOrEmpty(record?.name) ? record.name : "Free Play",
                kindWord = "Free Play", legIndex = 0, legCount = 1, runId = RunId(instanceId), owner = "title",
            };
            plan.rules.navigation = PlaylistRunPolicy.NavigationTitle;
            plan.rules.autoStart = true;
            var leg = new PlaylistRunLeg
            {
                id = !string.IsNullOrEmpty(leg0?.id) ? leg0.id : LegId, name = !string.IsNullOrEmpty(leg0?.name) ? leg0.name : "Free Play",
                beat = "climax", minutes = RoundSeconds / 60f, next = PlaylistRunRules.WalkOn,
                objective = new PlaylistRunObjective { type = PlaylistRunRules.Survive, seconds = RoundSeconds },
                objectiveText = !string.IsNullOrEmpty(leg0?.objectiveText) ? leg0.objectiveText : "Play anything you like!",
            };
            foreach (var s in stops)
            {
                var role = s.role == "start" || s.role == "end" ? "stage" : s.role;   // no start ring / finale in free play
                // Free play never times a stop (its own clock is the title's); bosses stay bosses for Dreamie.
                leg.stops.Add(new PlaylistRunStop
                {
                    occurrenceId = s.occ, attraction = s.attraction, role = role, standing = true,
                    challenge = role == "boss" || role == "miniboss" ? PlaylistChallenge.Boss : PlaylistChallenge.Moment,
                });
            }
            plan.legs.Add(leg);
            if (record != null) PlaylistRunPlanFromManifest.AttachAuthored(record, plan);
            plan.rules.navigation = PlaylistRunPolicy.NavigationTitle;   // whatever the record says, free play is the title's
            return plan;
        }

        /// The default record from a bundle fetched with no `?playlist` (pl-store: block.defaultPlaylist, else freeplay).
        public static PackagePlaylist Record(JSONObject bundle) =>
            bundle == null ? null : PlaylistRunPlanFromManifest.ParseInline(bundle.GetField("playlist"));

        static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }
    }
}
