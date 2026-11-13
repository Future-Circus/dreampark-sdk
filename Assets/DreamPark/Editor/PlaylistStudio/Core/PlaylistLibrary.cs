#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DreamPark.SpaceMap;
using Defective.JSON;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>One placeable attraction of the title, as the Studio's library shows it.</summary>
    public sealed class PlaylistLibraryItem
    {
        public string guid, leaf, resourceName, assetPath, displayName, size, previewPath;
        public bool hasPackingBake;
        /// Authored footprint (metres) and every smaller size it can be laid out at, largest first
        /// (layout frames + shrink) — the packer's size ladder (PLAYLISTS.md § 6.1 step 4).
        public Vector2 authored;
        public List<Vector2> ladder = new List<Vector2>();
        /// start | stage | miniboss | boss | end | display | prop (a hint for authoring and the generator).
        public string roleHint = "stage";
        /// Objective kinds this attraction can end (PackagePlaylist.Objectives) and where that came from:
        /// "catalog" (the title's objective catalog), "lua" (its scripts), "name" (its name only).
        public List<string> objectives = new List<string>();
        public string objectivesSource = "name";
        /// dp.playlist signals / end events its scripts already send (stage_won, boss_defeated, …).
        public List<string> luaSignals = new List<string>();
        /// Title badge ids earned here (.badges.json earnAt).
        public List<string> badges = new List<string>();
        /// Short facts for the generator (lua words, catalog note). Never shown as gameplay truth.
        public string notes = "";
        /// Challenge kinds it supports (PLAYLISTS.md § 12; live or hooked), each kind's readiness
        /// (live | hook | build), its catalog default and the designer's expected seconds. Empty = no catalog.
        public List<string> challenges = new List<string>();
        public Dictionary<string, string> challengeLevels = new Dictionary<string, string>();
        public string defaultChallenge;
        public int expectedSec;
        /// Realistic targets per challenge kind ({unit, par, max}; catalog `targets`) and the boss's name, if any.
        public Dictionary<string, PlaylistChallenges.Target> targets = new Dictionary<string, PlaylistChallenges.Target>(StringComparer.Ordinal);
        public string bossName;

        public Vector2 Minimum => ladder.Count > 0 ? ladder[ladder.Count - 1] : authored;
        public bool FitsSmallRoom => ladder.Any(v => PlaylistLibrary.FitsIn(v, PlaylistLibrary.SmallRoomW, PlaylistLibrary.SmallRoomD))
            || PlaylistLibrary.FitsIn(authored, PlaylistLibrary.SmallRoomW, PlaylistLibrary.SmallRoomD);
        public string Name => !string.IsNullOrEmpty(displayName) ? displayName : PlaylistLibrary.Pretty(leaf);
        public bool IsBossy => roleHint == "boss" || roleHint == "miniboss";

        public PlaylistAttractionRef ToRef() => new PlaylistAttractionRef
        {
            guid = guid, assetPath = assetPath, leaf = leaf, resourceName = resourceName, size = size, hasPackingBake = hasPackingBake,
        };
    }

    /// <summary>
    /// Pure helpers over the title's attractions: role and objective hints, the packer's footprint book,
    /// the validator's resolver, and the title context the generator sends to Web.
    /// </summary>
    public static class PlaylistLibrary
    {
        /// The fit matrix's smallest space and the generator's "small room": 12 × 18 ft.
        public const float SmallRoomW = 3.6576f, SmallRoomD = 5.4864f;

        public static bool FitsIn(Vector2 size, float w, float d)
            => size.x > 0.01f && size.y > 0.01f
               && ((size.x <= w + 1e-3f && size.y <= d + 1e-3f) || (size.y <= w + 1e-3f && size.x <= d + 1e-3f));

        /// The packer's ladder rule (PlaylistFootprintBook.AddLadder): authored first, then every smaller
        /// distinct size, largest area first.
        public static List<Vector2> Ladder(Vector2 authored, IEnumerable<Vector2> smaller)
        {
            var ladder = new List<Vector2>();
            if (!(authored.x > 0.05f && authored.y > 0.05f)) return ladder;
            ladder.Add(authored);
            foreach (Vector2 v in smaller ?? Enumerable.Empty<Vector2>())
            {
                if (!(v.x > 0.05f && v.y > 0.05f) || v.x * v.y > authored.x * authored.y + 1e-4f) continue;
                if (ladder.Any(u => Mathf.Abs(u.x - v.x) < 0.01f && Mathf.Abs(u.y - v.y) < 0.01f)) continue;
                ladder.Add(v);
            }
            ladder.Sort((a, b) => (b.x * b.y).CompareTo(a.x * a.y));
            return ladder;
        }

        // ── names ──

        /// "A_KingJubbo" → "King Jubbo" (the runner's rule for a stop without a displayName).
        public static string Pretty(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return "";
            string s = leaf;
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.Length > 2 && s[1] == '_' && char.IsUpper(s[0])) s = s.Substring(2);
            s = s.Replace('_', ' ');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
                    sb.Append(' ');
                sb.Append(c);
            }
            return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
        }

        static IEnumerable<string> Words(string s)
            => Regex.Split(Pretty(s ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0);

        static readonly HashSet<string> BossWords = new HashSet<string>
            { "boss", "king", "queen", "lord", "overlord", "warlord", "dragon", "titan", "showdown", "tyrant", "final" };
        static readonly HashSet<string> StartWords = new HashSet<string>
            { "start", "entrance", "lobby", "hub", "gate", "welcome", "spawn", "camp" };
        static readonly HashSet<string> EndWords = new HashSet<string>
            { "trophy", "victory", "finale", "results", "podium", "exit", "end", "escape" };

        /// A role hint from the prefab name and its scripts. The developer's catalog (or their edit) wins.
        public static string RoleHint(string leaf, string displayName, IEnumerable<string> luaTexts = null)
        {
            if (!string.IsNullOrEmpty(leaf) && leaf.StartsWith("P_", StringComparison.Ordinal)) return "prop";
            var words = new HashSet<string>(Words(leaf).Concat(Words(displayName)));
            bool mini = words.Contains("mini") || words.Contains("miniboss");
            if (words.Contains("miniboss") || (mini && words.Contains("boss"))) return "miniboss";
            if (words.Overlaps(BossWords)) return "boss";
            string lua = string.Join("\n", luaTexts ?? Enumerable.Empty<string>()).ToLowerInvariant();
            if (lua.Contains("boss_defeated") || lua.Contains("boss_won") || Regex.IsMatch(lua, "signal\\(\\s*['\"]boss")) return "boss";
            if (words.Overlaps(StartWords)) return "start";
            if (words.Overlaps(EndWords)) return "end";
            return "stage";
        }

        static readonly (string kind, Regex pattern)[] ObjectivePatterns =
        {
            ("defeat_boss", new Regex("boss_defeated|boss_won|defeat_boss|\\bboss\\b", RegexOptions.IgnoreCase)),
            ("collect", new Regex("\\bcoins?\\b|collect|pickup|\\bgems?\\b|medals?|stars?_collected", RegexOptions.IgnoreCase)),
            ("score", new Regex("\\bscore\\b|\\bpoints\\b|add_score|report_result", RegexOptions.IgnoreCase)),
            ("survive", new Regex("\\bwaves?\\b|surviv|\\blives\\b", RegexOptions.IgnoreCase)),
            ("reach_exit", new Regex("stage_won|exit_reached|\\bexit\\b|finish_line|\\bgoal\\b|\\bflag\\b|level_complete", RegexOptions.IgnoreCase)),
        };

        /// The objective kinds an attraction can end, inferred from its role and scripts (PackagePlaylist.Objectives
        /// order). `time` is always possible (a timed round). Used only when the title has no objective catalog.
        public static List<string> InferObjectives(string roleHint, IEnumerable<string> luaTexts, out string source)
        {
            var found = new HashSet<string>();
            string lua = string.Join("\n", luaTexts ?? Enumerable.Empty<string>());
            source = string.IsNullOrWhiteSpace(lua) ? "name" : "lua";
            foreach (var (kind, pattern) in ObjectivePatterns)
                if (pattern.IsMatch(lua)) found.Add(kind);
            if (roleHint == "boss" || roleHint == "miniboss") found.Add("defeat_boss");
            if (found.Count == 0 && roleHint != "display" && roleHint != "prop") found.Add("reach_exit");
            found.Add("time");
            return PackagePlaylist.Objectives.Where(found.Contains).ToList();
        }

        static readonly Regex SignalCall = new Regex("dp\\.playlist\\.(?:signal|stop_complete)\\s*\\(\\s*['\"]([a-z0-9_]{2,40})['\"]", RegexOptions.IgnoreCase);
        static readonly string[] KnownEvents = { "stage_won", "exit_reached", "boss_defeated", "boss_won", "coins", "collected", "survived", "won", "lost", "countdown_done", "report_result", "start_challenge" };

        /// Playlist signals the scripts send today, plus the standard end events they mention (§ 7.2).
        public static List<string> LuaSignals(IEnumerable<string> luaTexts)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string text in luaTexts ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (Match m in SignalCall.Matches(text)) found.Add(m.Groups[1].Value.ToLowerInvariant());
                foreach (string e in KnownEvents)
                    if (Regex.IsMatch(text, "(?<![A-Za-z0-9_])" + e + "(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)) found.Add(e);
            }
            return found.ToList();
        }

        public static string LeafOf(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return null;
            int slash = resourceName.LastIndexOf('/');
            return slash >= 0 ? resourceName.Substring(slash + 1) : resourceName;
        }

        // ── packer + validator plumbing ──

        public static Func<string, IReadOnlyList<PlaylistAttractionRef>> Resolver(IEnumerable<PlaylistLibraryItem> items)
            => ContentPlaylistValidator.Resolver(items.Select(i => i.ToRef()));

        /// The planner's footprint book for this title: every attraction's ladder under its leaf and
        /// resourceName, plus the platform bookends (as core's bridge does).
        public static PlaylistFootprintBook Book(string titleId, IEnumerable<PlaylistLibraryItem> items)
        {
            var book = new PlaylistFootprintBook();
            foreach (PlaylistLibraryItem i in items)
                if (i.ladder.Count > 0) book.AddLadder(titleId, i.authored, i.ladder.Skip(1).ToList(), i.leaf, i.resourceName);
            foreach (string id in PlaylistBookends.Ids)
                book.Add(PlaylistBookends.ContentId, PlaylistBookends.Footprint(id), PlaylistBookends.MinimumFootprint(id),
                    id, PlaylistBookends.AddressFor(id));
            return book;
        }

        // ── the generator's title context (Web POST /api/sdk/playlists/generate `context`) ──

        public sealed class BadgeInfo { public string id, name, description; public List<string> earnAt = new List<string>(); }

        /// Deterministic (sorted, rounded) so Web's cached title block is byte-stable between calls.
        public static JSONObject GeneratorContext(string titleId, string titleName, string description,
            IEnumerable<PlaylistKind> kinds, IEnumerable<PlaylistLibraryItem> items, IEnumerable<BadgeInfo> badges,
            string docs, ContentPlaylistStore.Data existing, IEnumerable<string> titleSignals = null)
        {
            var ctx = JSONObject.emptyObject;
            var title = JSONObject.emptyObject;
            title.AddField("id", titleId ?? "");
            title.AddField("name", string.IsNullOrEmpty(titleName) ? Pretty(titleId) : titleName);
            title.AddField("description", Cap(description, 2000));
            ctx.AddField("title", title);
            var k = JSONObject.emptyArray;
            foreach (PlaylistKind kind in kinds ?? Enumerable.Empty<PlaylistKind>())
            {
                var ko = JSONObject.emptyObject;
                ko.AddField("id", kind.id ?? ""); ko.AddField("singular", kind.singular ?? ""); ko.AddField("plural", kind.plural ?? "");
                k.Add(ko);
            }
            ctx.AddField("kinds", k);
            var list = JSONObject.emptyArray;
            foreach (PlaylistLibraryItem i in items.Where(i => i.hasPackingBake && i.roleHint != "prop")
                         .OrderBy(i => i.leaf, StringComparer.Ordinal).Take(400))
            {
                var a = JSONObject.emptyObject;
                a.AddField("attraction", i.leaf);
                a.AddField("resourceName", i.resourceName ?? "");
                a.AddField("displayName", i.Name);
                a.AddField("size", i.size ?? "");
                a.AddField("footprintM", Size(i.authored));
                a.AddField("minFootprintM", Size(i.Minimum));
                a.AddField("fitsSmallRoom", i.FitsSmallRoom);
                a.AddField("roleHint", i.roleHint ?? "stage");
                a.AddField("objectives", PlaylistStudioJson.StringArray(i.objectives));
                if (i.challenges.Count > 0) a.AddField("challenges", PlaylistStudioJson.StringArray(i.challenges));
                if (!string.IsNullOrEmpty(i.defaultChallenge)) a.AddField("defaultChallenge", i.defaultChallenge);
                if (i.expectedSec > 0) a.AddField("expectedSec", i.expectedSec);
                if (!string.IsNullOrEmpty(i.bossName)) a.AddField("bossName", Cap(i.bossName, 80));
                if (i.targets.Count > 0)
                {
                    var t = JSONObject.emptyObject;
                    foreach (var kv in i.targets.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        var x = JSONObject.emptyObject;
                        x.AddField("unit", Cap(kv.Value.unit, 40));
                        if (kv.Value.par > 0) PlaylistStudioJson.AddNumber(x, "par", kv.Value.par);
                        if (kv.Value.max > 0) PlaylistStudioJson.AddNumber(x, "max", kv.Value.max);
                        t.AddField(kv.Key, x);
                    }
                    a.AddField("targets", t);
                }
                a.AddField("badges", PlaylistStudioJson.StringArray(i.badges.OrderBy(b => b, StringComparer.Ordinal)));
                a.AddField("notes", Cap(i.notes, 600));
                list.Add(a);
            }
            ctx.AddField("attractions", list);
            var b = JSONObject.emptyArray;
            foreach (BadgeInfo badge in (badges ?? Enumerable.Empty<BadgeInfo>()).OrderBy(x => x.id, StringComparer.Ordinal).Take(200))
            {
                var bo = JSONObject.emptyObject;
                bo.AddField("id", badge.id ?? ""); bo.AddField("name", Cap(badge.name, 80));
                bo.AddField("description", Cap(badge.description, 240));
                bo.AddField("earnAt", PlaylistStudioJson.StringArray(badge.earnAt));
                b.Add(bo);
            }
            ctx.AddField("badges", b);
            var signals = new SortedSet<string>(items.SelectMany(i => i.luaSignals).Concat(titleSignals ?? Enumerable.Empty<string>()), StringComparer.Ordinal);
            ctx.AddField("luaSignals", PlaylistStudioJson.StringArray(signals));
            ctx.AddField("docs", Cap(docs, 40000));
            var ex = JSONObject.emptyArray;
            foreach (PackagePlaylist p in existing?.playlists ?? new List<PackagePlaylist>())
            {
                var eo = JSONObject.emptyObject;
                eo.AddField("id", p.id ?? ""); eo.AddField("name", p.name ?? ""); eo.AddField("kind", p.kind ?? "");
                eo.AddField("stops", PlaylistStudioJson.StringArray(p.arc.SelectMany(l => l.stops).Select(s => s.attraction)));
                ex.Add(eo);
            }
            ctx.AddField("existingPlaylists", ex);
            return ctx;
        }

        static JSONObject Size(Vector2 v)
        {
            var o = JSONObject.emptyObject;
            PlaylistStudioJson.AddNumber(o, "w", (float)Math.Round(v.x, 2));
            PlaylistStudioJson.AddNumber(o, "d", (float)Math.Round(v.y, 2));
            return o;
        }

        static string Cap(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);
    }
}
#endif
