#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// "Convert my Adventure / Sequence / Arena organizers into playlists" (pure). The organizers stay
    /// untouched and keep shipping; this only proposes DRAFT playlists that use the same attractions in the
    /// same order, with an arc read off the organizer (its start, its bosses, its end). What an organizer
    /// cannot say (a score target, a fiction-flavoured name) is left for the developer and shows as a
    /// validation message in the Studio — a migration never invents gameplay numbers.
    /// </summary>
    public static class PlaylistMigration
    {
        /// One organizer row: a single attraction, or a group ("world"/mission) of attractions.
        public sealed class Entry
        {
            public string name;          // group name ("Lava Caves"); null for a single attraction
            public bool isGroup;
            public bool hidden;
            public List<string> guids = new List<string>();
        }

        public sealed class Input
        {
            public string titleId, titleName;
            /// Adventure (.dreampark-sequence.json): explicit Start, rows, End. Null = no Adventure authored.
            public string adventureStartGuid, adventureEndGuid;
            public List<Entry> adventure;
            /// Sequence (.dreampark-dream-sequence.json): its groups (missions / worlds), level by level.
            public List<Entry> sequence;
            /// Arena (.dreampark-arena.json): candidate prefab guids in bucket + creator priority order
            /// (smallest bucket first), exclusions already removed, base frames only.
            public List<string> arena;
            public Dictionary<string, PlaylistLibraryItem> byGuid = new Dictionary<string, PlaylistLibraryItem>(StringComparer.Ordinal);
        }

        public sealed class Result
        {
            public readonly List<PlaylistKind> kinds = new List<PlaylistKind>();
            public readonly List<PackagePlaylist> playlists = new List<PackagePlaylist>();
            public readonly List<string> notes = new List<string>();
        }

        public static readonly PlaylistKind AdventureKind = PlaylistKind.Default();
        public static readonly PlaylistKind WorldKind = new PlaylistKind { id = "world", singular = "World", plural = "Worlds" };
        public static readonly PlaylistKind ChallengeKind = new PlaylistKind { id = "challenge", singular = "Challenge", plural = "Challenges" };

        public static Result Convert(Input input)
        {
            var result = new Result();
            string title = string.IsNullOrEmpty(input.titleName) ? PlaylistLibrary.Pretty(input.titleId) : input.titleName;

            // ── Adventure: one whole-place playlist, Start → rows → End ──
            if (input.adventure != null)
            {
                var stops = new List<(PlaylistLibraryItem item, string group)>();
                Add(stops, input, input.adventureStartGuid, null);
                foreach (Entry e in input.adventure.Where(e => !e.hidden))
                    foreach (string g in e.guids) Add(stops, input, g, e.isGroup ? e.name : null);
                Add(stops, input, input.adventureEndGuid, null);
                stops = stops.Where((s, i) => i == 0 || s.item != stops[i - 1].item).ToList();
                if (stops.Count > 0)
                {
                    PackagePlaylist p = BuildArc(input.titleId, "adventure", AdventureKind.id, title + " Adventure",
                        $"The original {title} adventure, from {stops[0].item.Name} to {stops[stops.Count - 1].item.Name}.",
                        Chunk(stops, groupLegs: true), "walk_on", result.notes);
                    p.tags = new List<string> { "adventure", "whole-house", "migrated" };
                    Use(result, AdventureKind);
                    result.playlists.Add(p);
                }
                else result.notes.Add("Adventure: no attractions resolved (its Start/End or rows point at missing prefabs).");
            }

            // ── Sequence: one playlist per multi-level group; single-attraction groups become one gauntlet ──
            if (input.sequence != null)
            {
                var singles = new List<(PlaylistLibraryItem item, string group)>();
                foreach (Entry e in input.sequence.Where(e => !e.hidden))
                {
                    var levels = new List<(PlaylistLibraryItem item, string group)>();
                    foreach (string g in e.guids) Add(levels, input, g, null);
                    levels = levels.Where(l => l.item.roleHint != "display" && l.item.roleHint != "prop").ToList();
                    if (levels.Count == 0) continue;
                    if (levels.Count == 1) { singles.Add((levels[0].item, e.name)); continue; }
                    string name = string.IsNullOrEmpty(e.name) ? levels[0].item.Name : e.name;
                    var legs = levels.Select(l => new List<(PlaylistLibraryItem, string)> { l }).ToList();
                    PackagePlaylist p = BuildArc(input.titleId, PlaylistStoreIds.Unique(Slug(name), result.playlists), WorldKind.id, name,
                        $"{name}, level by level{(levels.Any(l => l.item.IsBossy) ? ", all the way to " + levels.Last(l => l.item.IsBossy).item.Name : "")}.",
                        legs, "swap_in_place", result.notes);
                    p.tags = new List<string> { "world", "one-room", "migrated" };
                    Use(result, WorldKind);
                    result.playlists.Add(p);
                }
                // A gauntlet is made of games: hubs, intros and display rooms stay out; bosses play last.
                singles = singles.Where(x => x.item.roleHint == "stage" || x.item.IsBossy).OrderBy(x => x.item.IsBossy ? 1 : 0).ToList();
                if (singles.Count >= 2)
                {
                    var legs = singles.Select(s => new List<(PlaylistLibraryItem, string)> { (s.item, s.group) }).ToList();
                    PackagePlaylist p = BuildArc(input.titleId, PlaylistStoreIds.Unique("gauntlet", result.playlists), ChallengeKind.id,
                        title + " Gauntlet", $"Every {title} game, one after another in one room.", legs, "swap_in_place", result.notes);
                    // The old Sequence played each game as its own round: a timed gauntlet keeps that pace.
                    p.rules = new PlaylistRules { advance = "on_timer", timerSec = 120, scoring = "sum" };
                    foreach (PlaylistLeg leg in p.arc.Where(l => l.beat != "climax")) leg.objective = new PlaylistObjective { type = "time" };
                    p.tags = new List<string> { "gauntlet", "one-room", "migrated" };
                    Use(result, ChallengeKind);
                    result.playlists.Add(p);
                }
                else if (singles.Count == 1)
                {
                    var legs = new List<List<(PlaylistLibraryItem, string)>> { new List<(PlaylistLibraryItem, string)> { singles[0] } };
                    string name = string.IsNullOrEmpty(singles[0].group) ? singles[0].item.Name : singles[0].group;
                    PackagePlaylist p = BuildArc(input.titleId, PlaylistStoreIds.Unique(Slug(name), result.playlists), WorldKind.id, name,
                        $"{name} in one room.", legs, "swap_in_place", result.notes);
                    p.tags = new List<string> { "one-room", "migrated" };
                    Use(result, WorldKind);
                    result.playlists.Add(p);
                }
            }

            // ── Arena: freeplay buckets are not an arc; propose ONE showdown of its small-room games ──
            if (input.arena != null)
            {
                var picks = new List<(PlaylistLibraryItem item, string group)>();
                foreach (string g in input.arena) Add(picks, input, g, null);
                picks = picks.Where(p => p.item.roleHint != "prop" && p.item.roleHint != "display")
                    .GroupBy(p => p.item.guid).Select(x => x.First()).Take(6).ToList();
                if (picks.Count > 0)
                {
                    PlaylistLibraryItem boss = picks.Select(p => p.item).LastOrDefault(i => i.IsBossy);
                    var ordered = picks.Where(p => p.item != boss).ToList();
                    if (boss != null) ordered.Add((boss, null));
                    var legs = new List<List<(PlaylistLibraryItem, string)>> { ordered.Select(o => (o.item, o.group)).ToList() };
                    PackagePlaylist p = BuildArc(input.titleId, PlaylistStoreIds.Unique("arena-showdown", result.playlists), ChallengeKind.id,
                        title + " Showdown", $"The {title} arena in one round: everything at once, one winner.", legs, "walk_on", result.notes);
                    p.rules = new PlaylistRules { scoring = "sum" };
                    p.tags = new List<string> { "arena", "showdown", "migrated" };
                    result.notes.Add($"Arena → '{p.name}': freeplay had no end; set its score target (or switch the objective to survive with seconds).");
                    Use(result, ChallengeKind);
                    result.playlists.Add(p);
                }
            }
            return result;
        }

        static void Use(Result r, PlaylistKind k) { if (!r.kinds.Any(x => x.id == k.id)) r.kinds.Add(k); }

        static void Add(List<(PlaylistLibraryItem item, string group)> list, Input input, string guid, string group)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (input.byGuid.TryGetValue(guid, out PlaylistLibraryItem item) && item != null) list.Add((item, group));
        }

        /// Adventure rows → legs: a group is one leg; consecutive single attractions share one leg (≤ 4 stops);
        /// a boss always closes its leg, so the climax leg ends on the boss.
        static List<List<(PlaylistLibraryItem, string)>> Chunk(List<(PlaylistLibraryItem item, string group)> stops, bool groupLegs)
        {
            var legs = new List<List<(PlaylistLibraryItem, string)>>();
            List<(PlaylistLibraryItem, string)> cur = null; string curGroup = null;
            for (int i = 0; i < stops.Count; i++)
            {
                var s = stops[i];
                // The End Point is its own closing leg (a victory lap / trophy room after the climax).
                bool endPoint = i == stops.Count - 1 && i > 0 && s.item.roleHint == "end";
                bool newLeg = cur == null || endPoint || (groupLegs && s.group != curGroup) || cur.Count >= 4;
                if (newLeg) { cur = new List<(PlaylistLibraryItem, string)>(); legs.Add(cur); curGroup = s.group; }
                cur.Add(s);
                if (s.item.IsBossy) cur = null;
            }
            return legs;
        }

        /// The validator's 12-leg cap: merge the smallest adjacent pair of boss-free legs (≤ 12 stops together)
        /// until it fits; a merged pair plays its stops in one leg.
        static List<List<(PlaylistLibraryItem item, string group)>> FitLegCap(List<List<(PlaylistLibraryItem item, string group)>> legs, int cap = 12)
        {
            legs = legs.Select(l => l.ToList()).ToList();
            while (legs.Count > cap)
            {
                int best = -1, bestSize = int.MaxValue;
                for (int i = 0; i + 1 < legs.Count; i++)
                {
                    if (legs[i].Any(x => x.item.IsBossy) || legs[i + 1].Any(x => x.item.IsBossy)) continue;
                    int size = legs[i].Count + legs[i + 1].Count;
                    if (size <= 12 && size < bestSize) { best = i; bestSize = size; }
                }
                if (best < 0) break;
                legs[best].AddRange(legs[best + 1]);
                legs.RemoveAt(best + 1);
            }
            return legs;
        }

        /// The arc read off the order: opening first, the LAST boss leg is the climax (else the last leg),
        /// legs after it are the finale, the rest rise.
        static PackagePlaylist BuildArc(string titleId, string id, string kind, string name, string pitch,
            List<List<(PlaylistLibraryItem item, string group)>> legs, string next, List<string> notes)
        {
            legs = FitLegCap(legs);
            var p = new PackagePlaylist
            {
                id = id, owner = "title", titleId = titleId, kind = kind, name = Cap(name, 60), pitch = Cap(pitch, 200),
                fit = new PlaylistFit(), rules = new PlaylistRules(), arc = new List<PlaylistLeg>(),
            };
            int climax = legs.FindLastIndex(l => l.Any(s => s.item.IsBossy));
            if (climax < 0) climax = legs.Count > 1 && IsEndLeg(legs[legs.Count - 1]) ? legs.Count - 2 : legs.Count - 1;
            var badges = new List<string>();
            for (int i = 0; i < legs.Count; i++)
            {
                var stops = legs[i];
                string beat = i == climax ? "climax" : i > climax ? "finale" : i == 0 ? "opening" : "rising";
                var leg = new PlaylistLeg
                {
                    id = "leg" + (i + 1),
                    name = Cap(stops.Select(s => s.group).FirstOrDefault(g => !string.IsNullOrEmpty(g))
                        ?? (beat == "climax" && stops.Any(s => s.item.IsBossy) ? "FINAL BOSS: " + stops.Last(s => s.item.IsBossy).item.Name : stops[0].item.Name), 60),
                    beat = beat,
                    next = next,
                    stops = new List<PlaylistStop>(),
                };
                for (int j = 0; j < stops.Count; j++)
                {
                    PlaylistLibraryItem item = stops[j].item;
                    string role = item.roleHint == "boss" || item.roleHint == "miniboss" ? item.roleHint
                        : i == 0 && j == 0 ? "start"
                        : i == legs.Count - 1 && j == stops.Count - 1 && item.roleHint == "end" ? "end" : "stage";
                    string prefer = i == 0 && j == 0 ? "entrance" : role == "boss" ? "biggest" : "any";
                    leg.stops.Add(new PlaylistStop
                    {
                        attraction = item.leaf, role = role, alternates = new List<string>(),
                        placement = new PlaylistPlacement { prefer = prefer },
                    });
                    badges.AddRange(item.badges);
                }
                leg.minutes = Math.Max(1, stops.Sum(s => s.item.IsBossy ? 5 : s.item.roleHint == "start" || s.item.roleHint == "end" ? 1 : 3));
                PlaylistLibraryItem lastBoss = stops.Select(s => s.item).LastOrDefault(it => it.IsBossy);
                PlaylistLibraryItem lastStop = stops[stops.Count - 1].item;
                if (lastBoss != null) leg.objective = new PlaylistObjective { type = "defeat_boss", target = lastBoss.leaf };
                else if (beat == "finale" && IsEndLeg(stops))
                    leg.objective = new PlaylistObjective { type = "time", seconds = 60 };   // a display room: a timed victory lap
                else if (beat == "climax" && CanHoldOut(lastStop))
                {
                    // No boss, but the catalog says this stop is a hold-out / wave: survive its designed length.
                    leg.objective = new PlaylistObjective { type = "survive", seconds = lastStop.expectedSec };
                    notes.Add($"'{p.name}': its climax '{leg.name}' has no boss — it is a {lastStop.expectedSec} s hold-out (the catalog's expected length); tune it.");
                }
                else if (beat == "climax")
                {
                    // No boss to end on: a score showdown. The catalog's par (realistic target) when it has one, else an
                    // honest gap the developer fills.
                    var pars = stops.Select(s => s.item.targets.TryGetValue("score", out var t) ? t.par : 0f).ToList();
                    int par = pars.All(x => x > 0) ? (int)Math.Round(pars.Sum()) : 0;
                    leg.objective = new PlaylistObjective { type = "score", count = par };
                    notes.Add(par > 0
                        ? $"'{p.name}': its climax '{leg.name}' has no boss — a score showdown at the catalog's par ({par} {stops[stops.Count - 1].item.targets["score"].unit}); tune it."
                        : $"'{p.name}': its climax '{leg.name}' has no boss — set the score target it ends on.");
                }
                else leg.objective = new PlaylistObjective { type = "reach_exit" };
                p.arc.Add(leg);
            }
            p.badges = badges.Distinct().ToList();
            // An honest fit hint: the largest smallest-size stop must stand somewhere.
            float minArea = legs.SelectMany(l => l).Select(s => s.item.Minimum.x * s.item.Minimum.y).DefaultIfEmpty(0f).Max();
            p.fit = new PlaylistFit { minRooms = 1, minAreaM2 = (float)Math.Ceiling(minArea) };
            ContentPlaylistStore.Normalize(p);
            return p;
        }

        static bool IsEndLeg(List<(PlaylistLibraryItem item, string group)> leg) => leg.Count == 1 && leg[0].item.roleHint == "end";

        /// The catalog (§ 12) says the stop can be held out: a survive / clear / protect kind and a designed length.
        static bool CanHoldOut(PlaylistLibraryItem item)
            => item.expectedSec > 0 && (item.challenges.Contains("survive") || item.defaultChallenge == "survive"
                || item.defaultChallenge == "clear" || item.defaultChallenge == "protect")
               && !item.targets.ContainsKey("score");

        static string Slug(string s) { string id = PlaylistProposals.Slug(s); return string.IsNullOrEmpty(id) ? "playlist" : id; }
        static string Cap(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        static class PlaylistStoreIds
        {
            public static string Unique(string id, List<PackagePlaylist> taken)
            {
                if (!taken.Any(p => p.id == id)) return id;
                int n = 2;
                while (taken.Any(p => p.id == id + "-" + n)) n++;
                return id + "-" + n;
            }
        }
    }
}
#endif
