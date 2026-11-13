#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// Challenges (PLAYLISTS.md § 12, objective-catalog PROPOSAL): what kind of objective a stop is. The kinds
    /// table mirrors dreampark-core Tools/Dreamie/objectives/catalog.json `kinds`; the per-title catalog is the
    /// title's `.dreampark-challenges.json` (§ 12.2), or the title's section of the first-party catalog.json.
    /// </summary>
    public static class PlaylistChallenges
    {
        public sealed class Kind
        {
            public string id, name, type;          // type: challenge | moment | modifier
            public string[] runnerObjectives;      // the leg objective(s) it maps to (§ 12.1 "derived")
            public string goal;
        }

        public static readonly Kind[] Kinds =
        {
            K("collect", "Coin Rush", "challenge", "Collect all {of} {unit}!", "collect"),
            K("reach_end", "Make It Across", "challenge", "Make it across {name}!", "reach_exit"),
            K("boss", "Boss Battle", "challenge", "Defeat {name}!", "defeat_boss"),
            K("clear", "Clear the Wave", "challenge", "Clear {name}!", "reach_exit"),
            K("survive", "Hold Out", "challenge", "Survive {seconds} seconds!", "survive"),
            K("score", "Score Attack", "challenge", "Score {count} in {name}!", "score"),
            K("race", "Race", "challenge", "Win the race at {name}!", "reach_exit"),
            K("versus", "Showdown", "challenge", "Win the match at {name}!", "reach_exit"),
            K("catch", "Catch", "challenge", "Catch {count} critters!", "collect"),
            K("find", "Treasure Hunt", "challenge", "Find the {count} hidden {unit}!", "collect"),
            K("solve", "Puzzle", "challenge", "Solve {name}!", "reach_exit"),
            K("protect", "Defend & Deliver", "challenge", "Bring all {of} {unit} home!", "survive", "collect"),
            K("moment", "Story Moment", "moment", "Enjoy {name}!", "time", "reach_exit"),
            K("no_hit", "Untouchable", "modifier", "…without getting hit!"),
            K("combo", "Combo", "modifier", "…and chain a {count}x combo!"),
            K("clock", "Beat the Clock", "modifier", "…in under {seconds} seconds!"),
        };

        static Kind K(string id, string name, string type, string goal, params string[] objectives)
            => new Kind { id = id, name = name, type = type, goal = goal, runnerObjectives = objectives };

        public static Kind Find(string id) => Kinds.FirstOrDefault(k => k.id == id);
        public static bool IsModifier(string id) => Find(id)?.type == "modifier";
        public static string Name(string id) => Find(id)?.name ?? id;

        /// § 12.1 "absent = derived": the stop's challenge from its role and the leg objective.
        public static string Derive(string role, string legObjective, string catalogDefault = null)
        {
            if (!string.IsNullOrEmpty(catalogDefault)) return catalogDefault;
            if (role == "boss" || role == "miniboss") return "boss";
            switch (legObjective)
            {
                case "collect": return "collect";
                case "score": return "score";
                case "survive": return "survive";
                case "time": return "moment";
                case "defeat_boss": return "boss";
                default: return "reach_end";
            }
        }

        /// One attraction's row of the title catalog.
        /// A realistic target for one kind (catalog `targets.{kind}` / the row's own unit-par-max for its default kind).
        public sealed class Target { public string unit; public float par, max; }   // max 0 = no ceiling

        public sealed class Row
        {
            public string defaultKind, unit, note, roleHint, bossName, bossSignal, bossWhen;
            public int expectedSec;
            public readonly Dictionary<string, Target> targets = new Dictionary<string, Target>(StringComparer.Ordinal);
            /// kind id → readiness: live (the title reports it) | hook (a few Lua lines) | build (new logic).
            public readonly Dictionary<string, string> kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            public IEnumerable<string> Supported => kinds.Where(k => k.Value != "build").Select(k => k.Key);
        }

        /// Reads either shape, keyed by prefab file name:
        ///  • § 12.2 `.dreampark-challenges.json`: {"attractions": {"A_X": {"default", "supports": [...], "unit", "expectedSec"}}}
        ///    (`supports` = live or hooked);
        ///  • the first-party catalog: the whole catalog.json (pass titleId) or one title's section:
        ///    {"attractions": [{"attraction", "default", "expectedSec", "kinds": {"collect": {"level": "live"}}, "note"}]}.
        public static Dictionary<string, Row> ReadCatalog(string json, string titleId = null)
        {
            var map = new Dictionary<string, Row>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return map;
            JSONObject root;
            try { root = new JSONObject(json); } catch { return map; }
            if (root == null || root.type != JSONObject.Type.Object) return map;
            JSONObject titles = root.GetField("titles");
            if (titles != null && !string.IsNullOrEmpty(titleId)) root = titles.GetField(titleId) ?? root;
            JSONObject attractions = root.GetField("attractions");
            if (attractions == null) return map;
            if (attractions.type == JSONObject.Type.Object && attractions.keys != null)
                for (int i = 0; i < attractions.count; i++) Add(map, attractions.keys[i], attractions.list[i]);
            else
                foreach (JSONObject row in PlaylistStudioJson.Items(attractions))
                    Add(map, PlaylistStudioJson.Str(row, "attraction") ?? PlaylistLibrary.LeafOf(PlaylistStudioJson.Str(row, "resourceName")), row);
            return map;
        }

        static void Add(Dictionary<string, Row> map, string key, JSONObject o)
        {
            if (string.IsNullOrEmpty(key) || o == null || o.type != JSONObject.Type.Object) return;
            var row = new Row
            {
                defaultKind = PlaylistStudioJson.Str(o, "default"),
                unit = PlaylistStudioJson.Str(o, "unit") ?? "",
                note = PlaylistStudioJson.Str(o, "note") ?? PlaylistStudioJson.Str(o, "notes") ?? "",
                expectedSec = PlaylistStudioJson.Int(o, "expectedSec"),
                roleHint = PlaylistStudioJson.Str(o, "roleHint") ?? PlaylistStudioJson.Str(o, "role"),
            };
            JSONObject boss = o.GetField("boss");
            if (boss != null && boss.type == JSONObject.Type.Object)
            { row.bossName = PlaylistStudioJson.Str(boss, "name") ?? ""; row.bossSignal = PlaylistStudioJson.Str(boss, "signal"); row.bossWhen = PlaylistStudioJson.Str(boss, "when"); }
            else if (boss != null && boss.type == JSONObject.Type.String) row.bossName = boss.stringValue;
            JSONObject targets = o.GetField("targets");
            if (targets != null && targets.type == JSONObject.Type.Object && targets.keys != null)
                for (int i = 0; i < targets.count; i++)
                    if (Find(targets.keys[i]) != null && targets.list[i].type == JSONObject.Type.Object)
                        row.targets[targets.keys[i]] = new Target
                        {
                            unit = PlaylistStudioJson.Str(targets.list[i], "unit") ?? "",
                            par = PlaylistStudioJson.Float(targets.list[i], "par"), max = PlaylistStudioJson.Float(targets.list[i], "max"),
                        };
            // The row's own unit / par / max describe its default kind.
            float par = PlaylistStudioJson.Float(o, "par"), max = PlaylistStudioJson.Float(o, "max");
            if (row.defaultKind != null && Find(row.defaultKind) != null && !row.targets.ContainsKey(row.defaultKind) && (par > 0 || max > 0))
                row.targets[row.defaultKind] = new Target { unit = row.unit, par = par, max = max };
            foreach (string k in PlaylistStudioJson.Strings(o.GetField("supports")))
                if (Find(k) != null) row.kinds[k] = "live";
            JSONObject kinds = o.GetField("kinds");
            if (kinds != null && kinds.type == JSONObject.Type.Object && kinds.keys != null)
                for (int i = 0; i < kinds.count; i++)
                    if (Find(kinds.keys[i]) != null)
                        row.kinds[kinds.keys[i]] = PlaylistStudioJson.Str(kinds.list[i], "level") ?? "hook";
            if (row.defaultKind != null && Find(row.defaultKind) == null) row.defaultKind = null;
            if (row.kinds.Count > 0 || row.defaultKind != null || row.bossName != null || row.roleHint != null) map[key] = row;
        }

        /// § 12.2 shape for a title, from a catalog's rows (the "Import catalog" button writes this).
        public static string WriteTitleFile(Dictionary<string, Row> rows)
        {
            var root = JSONObject.emptyObject;
            root.AddField("schemaVersion", 1);
            var attractions = JSONObject.emptyObject;
            foreach (var kv in rows.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var o = JSONObject.emptyObject;
                if (!string.IsNullOrEmpty(kv.Value.defaultKind)) o.AddField("default", kv.Value.defaultKind);
                o.AddField("supports", PlaylistStudioJson.StringArray(kv.Value.Supported));
                if (!string.IsNullOrEmpty(kv.Value.unit)) o.AddField("unit", kv.Value.unit);
                if (kv.Value.expectedSec > 0) o.AddField("expectedSec", kv.Value.expectedSec);
                if (!string.IsNullOrEmpty(kv.Value.note)) o.AddField("note", kv.Value.note);
                if (!string.IsNullOrEmpty(kv.Value.roleHint)) o.AddField("roleHint", kv.Value.roleHint);
                if (kv.Value.bossName != null)
                {
                    var b = JSONObject.emptyObject; b.AddField("name", kv.Value.bossName);
                    if (!string.IsNullOrEmpty(kv.Value.bossSignal)) b.AddField("signal", kv.Value.bossSignal);
                    o.AddField("boss", b);
                }
                if (kv.Value.targets.Count > 0)
                {
                    var t = JSONObject.emptyObject;
                    foreach (var tk in kv.Value.targets.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        var x = JSONObject.emptyObject;
                        if (!string.IsNullOrEmpty(tk.Value.unit)) x.AddField("unit", tk.Value.unit);
                        if (tk.Value.par > 0) PlaylistStudioJson.AddNumber(x, "par", tk.Value.par);
                        if (tk.Value.max > 0) PlaylistStudioJson.AddNumber(x, "max", tk.Value.max);
                        t.AddField(tk.Key, x);
                    }
                    o.AddField("targets", t);
                }
                attractions.AddField(kv.Key, o);
            }
            root.AddField("attractions", attractions);
            return PlaylistStudioJson.Pretty(root) + "\n";
        }

        /// Applies a catalog row to a library item: its challenge kinds and the leg objectives they can end.
        public static void Apply(PlaylistLibraryItem item, Row row)
        {
            if (item == null || row == null) return;
            item.challenges = row.kinds.Where(k => k.Value != "build").Select(k => k.Key).ToList();
            item.challengeLevels = new Dictionary<string, string>(row.kinds);
            item.defaultChallenge = row.defaultKind;
            // The catalog knows the roles and the bosses even when the prefab name does not say so.
            if (!string.IsNullOrEmpty(row.roleHint) && Array.IndexOf(new[] { "start", "stage", "miniboss", "boss", "end", "display", "prop" }, row.roleHint) >= 0)
                item.roleHint = row.roleHint;
            if ((row.bossName != null || row.defaultKind == "boss") && !item.IsBossy) item.roleHint = "boss";
            item.bossName = row.bossName;
            if (!string.IsNullOrEmpty(row.bossName))
                item.notes = $"Boss: {row.bossName}{(string.IsNullOrEmpty(row.bossWhen) ? "" : " (" + row.bossWhen + ")")}" + (string.IsNullOrEmpty(item.notes) ? "" : "; " + item.notes);
            foreach (var t in row.targets) item.targets[t.Key] = t.Value;
            item.expectedSec = row.expectedSec;
            var objectives = new HashSet<string>(item.challenges.SelectMany(k => Find(k)?.runnerObjectives ?? Array.Empty<string>())) { "time" };
            if (item.IsBossy) objectives.Add("defeat_boss");
            item.objectives = PackagePlaylist.Objectives.Where(objectives.Contains).ToList();
            item.objectivesSource = "catalog";
            if (!string.IsNullOrEmpty(row.note)) item.notes = string.IsNullOrEmpty(item.notes) ? row.note : row.note + "; " + item.notes;
        }

        /// § 12.1 validation, warnings only: unknown kind, a kind the catalog does not list, a bonus that is not a
        /// modifier, more than 2 bonuses.
        public static List<PlaylistIssue> Validate(PackagePlaylist p, Func<string, PlaylistLibraryItem> item)
        {
            var issues = new List<PlaylistIssue>();
            foreach (PlaylistLeg leg in p.arc)
                foreach (PlaylistStop s in leg.stops)
                {
                    if (!string.IsNullOrEmpty(s.challenge))
                    {
                        Kind k = Find(s.challenge);
                        if (k == null || k.type == "modifier") issues.Add(W(p.id, $"stop '{s.attraction}': challenge '{s.challenge}' is not a catalog kind."));
                        else
                        {
                            PlaylistLibraryItem it = item?.Invoke(s.attraction);
                            if (it != null && it.challenges.Count > 0 && !it.challenges.Contains(s.challenge))
                                issues.Add(W(p.id, $"stop '{s.attraction}': its catalog does not list '{s.challenge}' ({string.Join(", ", it.challenges)})."));
                        }
                    }
                    var bonus = s.bonus ?? new List<string>();
                    if (bonus.Count > 2) issues.Add(W(p.id, $"stop '{s.attraction}': more than 2 bonuses."));
                    foreach (string b in bonus)
                        if (!IsModifier(b)) issues.Add(W(p.id, $"stop '{s.attraction}': bonus '{b}' is not a modifier (no_hit, combo, clock)."));
                }
            return issues;
        }

        static PlaylistIssue W(string id, string m) => new PlaylistIssue { playlistId = id, isError = false, message = m };
    }
}
#endif
