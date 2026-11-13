#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Defective.JSON;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// Chapters & unlocks in the CANONICAL shape (PLAYLISTS.md § 2.1 = § 15.1, critters-story + playlist-next):
    /// <code>
    /// "requires": ["lava-caves", "Zombiez/the-mall", "title:Zombiez:the-mall",      // that playlist won
    ///              {"badge": "sal_coin_master"}, {"flag": "met-maple"}, {"flag": "cr.partner", "is": "fire"},
    ///              {"min": 2, "of": ["a", "b", {"badge": "x"}]}]                     // ALL entries; [] = always open
    /// "sets": {"flags": {"lava-cleared": true, "cr.partner": "fire"}, "badges": ["sal_champion"]}   // on a WON run
    /// "continueWith": ["snowlands"]                                                   // suggested next; never locks
    /// </code>
    /// In memory this follows playlist-model's carrier (ContentPlaylistStore.AuthoringAsText): the canonical JSON
    /// lives in `PackagePlaylist.requiresJson` / `setsJson` (full fidelity; `windowJson` / `personalJson` are carried
    /// untouched), and `requires` / `sets` hold the lossy projection ("playlist:/badge:/flag:" refs, flag ids).
    /// Read gates with <see cref="Requires"/> / <see cref="Sets"/>, write them with <see cref="SetRequires"/> /
    /// <see cref="SetSets"/>: both keep the two in step. The first Studio proposal (prefixed strings, sets as a list
    /// of names) is still read.
    /// </summary>
    public static class PlaylistGates
    {
        public const string ContinueWithField = "continueWith", RequiresField = "requires", SetsField = "sets", ChapterField = "chapter";
        public const string PlaylistRef = "playlist", BadgeRef = "badge", FlagRef = "flag", MinRef = "min";
        static readonly Regex FlagId = new Regex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$");

        // ── entries ──

        public sealed class Entry
        {
            public string kind;            // playlist | badge | flag | min
            public string id;              // playlist id (maybe "T/id" or "title:T:id"), badge id or flag name
            public string value;           // {flag, is}
            public int min;
            public List<Entry> of = new List<Entry>();
            public string Title => kind != PlaylistRef ? null : id.StartsWith("title:", StringComparison.Ordinal) ? id.Split(':')[1]
                : id.Contains("/") ? id.Substring(0, id.IndexOf('/')) : null;
            public string LocalId => kind != PlaylistRef ? id : id.StartsWith("title:", StringComparison.Ordinal) ? id.Substring(id.LastIndexOf(':') + 1)
                : id.Contains("/") ? id.Substring(id.IndexOf('/') + 1) : id;
        }

        /// One in-memory entry string → its model (null when unreadable).
        public static Entry Parse(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return null;
            string e = entry.Trim();
            if (!e.StartsWith("{", StringComparison.Ordinal))
            {
                // The first SDK proposal's prefixed strings.
                if (e.StartsWith("playlist:", StringComparison.Ordinal)) return new Entry { kind = PlaylistRef, id = e.Substring(9) };
                if (e.StartsWith("badge:", StringComparison.Ordinal)) return new Entry { kind = BadgeRef, id = e.Substring(6) };
                if (e.StartsWith("flag:", StringComparison.Ordinal)) return new Entry { kind = FlagRef, id = e.Substring(5) };
                return new Entry { kind = PlaylistRef, id = e };
            }
            return FromJson(SafeParse(e));
        }

        static Entry FromJson(JSONObject o)
        {
            if (o == null) return null;
            if (o.type == JSONObject.Type.String) return Parse(o.stringValue);
            if (o.type != JSONObject.Type.Object) return null;
            if (o.HasField("min") || o.HasField("of"))
            {
                var m = new Entry { kind = MinRef, min = Math.Max(0, PlaylistStudioJson.Int(o, "min")) };
                foreach (JSONObject x in PlaylistStudioJson.Items(o.GetField("of"))) { Entry c = FromJson(x); if (c != null) m.of.Add(c); }
                return m;
            }
            string badge = PlaylistStudioJson.Str(o, "badge"), flag = PlaylistStudioJson.Str(o, "flag");
            string type = PlaylistStudioJson.Str(o, "type"), id = PlaylistStudioJson.Str(o, "id");
            if (badge != null) return new Entry { kind = BadgeRef, id = badge };
            if (flag != null) return new Entry { kind = FlagRef, id = flag, value = PlaylistStudioJson.Str(o, "is") };
            if (type != null && id != null) return Parse(type + ":" + id);
            string playlist = PlaylistStudioJson.Str(o, "playlist");
            return playlist != null ? new Entry { kind = PlaylistRef, id = playlist } : null;
        }

        static JSONObject ToJson(Entry e)
        {
            switch (e.kind)
            {
                case PlaylistRef: return JSONObject.StringObject(e.id);
                case BadgeRef: { var o = JSONObject.emptyObject; o.AddField("badge", e.id); return o; }
                case FlagRef: { var o = JSONObject.emptyObject; o.AddField("flag", e.id); if (!string.IsNullOrEmpty(e.value)) o.AddField("is", e.value); return o; }
                default:
                {
                    var o = JSONObject.emptyObject; o.AddField("min", e.min);
                    var of = JSONObject.emptyArray; foreach (Entry c in e.of) of.Add(ToJson(c));
                    o.AddField("of", of);
                    return o;
                }
            }
        }

        /// The in-memory string of an entry (canonical: a playlist ref as is, else compact JSON).
        public static string Encode(Entry e) => e == null ? null : e.kind == PlaylistRef ? e.id : ToJson(e).Print();
        public static string Ref(string kind, string id)
            => Encode(kind == MinRef ? null : new Entry { kind = kind, id = id });

        /// Simple entries only (playlist / badge / flag); false for {min, of} or unreadable.
        public static bool TryParse(string entry, out string kind, out string id)
        {
            Entry e = Parse(entry);
            kind = e?.kind; id = e?.id;
            return e != null && e.kind != MinRef;
        }

        // ── JSON in / out ──

        public static List<string> ReadRequires(JSONObject value)
        {
            var list = new List<string>();
            if (value == null) return list;
            IEnumerable<JSONObject> items = value.type == JSONObject.Type.Array ? PlaylistStudioJson.Items(value)
                : value.type == JSONObject.Type.Object && value.HasField("all") ? PlaylistStudioJson.Items(value.GetField("all"))
                : new[] { value };
            foreach (JSONObject x in items) { string s = Encode(FromJson(x)); if (s != null && !list.Contains(s)) list.Add(s); }
            return list;
        }

        public static JSONObject WriteRequires(IEnumerable<string> requires)
        {
            var a = JSONObject.emptyArray;
            foreach (string s in requires ?? Enumerable.Empty<string>()) { Entry e = Parse(s); if (e != null) a.Add(ToJson(e)); }
            return a;
        }

        /// `sets`: {flags: {name: true|"value"}, badges: [ids]} → "flag:name" / "flag:name=value" / "badge:id".
        /// Also reads a plain list of flag names (the first proposal).
        public static List<string> ReadSets(JSONObject value)
        {
            var list = new List<string>();
            if (value == null) return list;
            if (value.type == JSONObject.Type.Array)
                foreach (string f in PlaylistStudioJson.Strings(value))
                    list.Add(f.StartsWith("flag:") || f.StartsWith("badge:") ? f : "flag:" + f);
            else if (value.type == JSONObject.Type.Object)
            {
                JSONObject flags = value.GetField("flags");
                if (flags?.keys != null)
                    for (int i = 0; i < flags.count; i++)
                    {
                        JSONObject v = flags.list[i];
                        if (v.type == JSONObject.Type.Bool && !v.boolValue) continue;
                        list.Add("flag:" + flags.keys[i] + (v.type == JSONObject.Type.String ? "=" + v.stringValue : ""));
                    }
                foreach (string b in PlaylistStudioJson.Strings(value.GetField("badges"))) list.Add("badge:" + b);
            }
            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        public static JSONObject WriteSets(IEnumerable<string> sets)
        {
            var o = JSONObject.emptyObject;
            var flags = JSONObject.emptyObject;
            var badges = JSONObject.emptyArray;
            foreach (string s in sets ?? Enumerable.Empty<string>())
            {
                if (s.StartsWith("badge:")) badges.Add(s.Substring(6));
                else
                {
                    string f = s.StartsWith("flag:") ? s.Substring(5) : s;
                    int eq = f.IndexOf('=');
                    if (eq > 0) flags.AddField(f.Substring(0, eq), f.Substring(eq + 1)); else flags.AddField(f, true);
                }
            }
            if (flags.count > 0) o.AddField("flags", flags);
            if (badges.count > 0) o.AddField("badges", badges);
            return o;
        }

        // ── the playlist's gates (canonical *Json first, else the projection) ──

        /// Carrier text → JSON without ever throwing (Defective.JSON throws, and logs in Unity, on a bare "[]" / "{}"
        /// or garbage): empty containers come back empty, unreadable text comes back null (= none).
        public static JSONObject SafeParse(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            if (t == "[]") return JSONObject.emptyArray;
            if (t == "{}") return JSONObject.emptyObject;
            if (!(t.StartsWith("[") && t.EndsWith("]")) && !(t.StartsWith("{") && t.EndsWith("}"))) return null;
            try { return new JSONObject(t); } catch { return null; }
        }

        public static List<Entry> Requires(PackagePlaylist p)
        {
            if (p == null) return new List<Entry>();
            if (!string.IsNullOrEmpty(p.requiresJson))
            {
                JSONObject v = SafeParse(p.requiresJson);
                if (v != null) return ReadRequires(v).Select(Parse).Where(e => e != null).ToList();
            }
            return (p.requires ?? new List<string>()).Select(Parse).Where(e => e != null).ToList();
        }

        public static void SetRequires(PackagePlaylist p, IEnumerable<Entry> entries)
        {
            var list = entries.Where(e => e != null).ToList();
            p.requiresJson = list.Count == 0 ? "" : WriteRequires(list.Select(Encode)).Print();
            p.requires = list.Where(e => e.kind != MinRef).Select(e => e.kind + ":" + e.id).ToList();   // playlist-model's projection
        }

        /// `sets` as (flag name → value or null) and badge ids.
        public static (List<(string flag, string value)> flags, List<string> badges) Sets(PackagePlaylist p)
        {
            var internalList = new List<string>();
            if (!string.IsNullOrEmpty(p?.setsJson)) internalList = ReadSets(SafeParse(p.setsJson));
            else internalList = (p?.sets ?? new List<string>()).Select(f => f.StartsWith("flag:") || f.StartsWith("badge:") ? f : "flag:" + f).ToList();
            var flags = internalList.Where(x => x.StartsWith("flag:")).Select(x => x.Substring(5))
                .Select(f => f.Contains("=") ? (f.Substring(0, f.IndexOf('=')), f.Substring(f.IndexOf('=') + 1)) : (f, (string)null)).ToList();
            return (flags, internalList.Where(x => x.StartsWith("badge:")).Select(x => x.Substring(6)).ToList());
        }

        public static void SetSets(PackagePlaylist p, IEnumerable<string> flags, IEnumerable<string> badges)
        {
            var internalList = flags.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => "flag:" + f.Trim())
                .Concat(badges.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => "badge:" + b.Trim())).Distinct().ToList();
            JSONObject o = WriteSets(internalList);
            p.setsJson = o.count == 0 ? "" : o.Print();
            p.sets = internalList.Where(x => x.StartsWith("flag:")).Select(x => x.Substring(5).Split('=')[0]).ToList();   // projection: flag ids
        }

        public static IEnumerable<string> SetFlags(PackagePlaylist p) => Sets(p).flags.Select(f => f.value == null ? f.flag : f.flag + "=" + f.value);
        public static IEnumerable<string> SetBadges(PackagePlaylist p) => Sets(p).badges;

        public static bool IsStarter(PackagePlaylist p) => Requires(p).Count == 0;

        static IEnumerable<Entry> Flatten(Entry e)
        {
            if (e == null) yield break;
            yield return e;
            foreach (Entry c in e.of) foreach (Entry x in Flatten(c)) yield return x;
        }

        /// § 15.5. Errors: an unknown playlist entry (this title) or continueWith id; a malformed entry; an impossible
        /// {min, of}; a requires cycle; no always-open playlist. Warnings: the headline is locked; a badge that is not
        /// the title's; a flag nothing sets (only Lua could); a sets.badges id the title lacks.
        public static List<PlaylistIssue> Validate(ContentPlaylistStore.Data data, ISet<string> badgeIds = null, string titleId = null)
        {
            var issues = new List<PlaylistIssue>();
            var list = data?.playlists ?? new List<PackagePlaylist>();
            if (list.Count == 0) return issues;
            var ids = new HashSet<string>(list.Select(p => p.id).Where(i => !string.IsNullOrEmpty(i)), StringComparer.Ordinal);
            var flagsSet = new HashSet<string>(list.SelectMany(p => SetFlags(p).Select(f => f.Split('=')[0])), StringComparer.Ordinal);
            bool Local(Entry e) => e.Title == null || e.Title == titleId;
            foreach (PackagePlaylist p in list)
            {
                string pid = string.IsNullOrEmpty(p.id) ? "(no id)" : p.id;
                foreach (string next in p.continueWith ?? new List<string>())
                {
                    if (next == p.id) issues.Add(Warn(pid, "continueWith names itself."));
                    else if (!ids.Contains(next) && !next.Contains("/") && !next.StartsWith("title:")) issues.Add(Err(pid, $"continueWith '{next}' is not a playlist of this title."));
                }
                foreach (Entry root in Requires(p))
                {
                    foreach (Entry e in Flatten(root))
                    {
                        if (e.kind == MinRef && (e.min < 1 || e.min > e.of.Count)) issues.Add(Err(pid, $"requires {{min: {e.min}}} of {e.of.Count} entries can never hold."));
                        if (e.kind == PlaylistRef && Local(e) && !ids.Contains(e.LocalId)) issues.Add(Err(pid, $"requires playlist '{e.LocalId}', which this title does not have."));
                        if (e.kind == PlaylistRef && Local(e) && e.LocalId == p.id) issues.Add(Err(pid, "requires itself (it could never unlock)."));
                        if (e.kind == BadgeRef && badgeIds != null && !badgeIds.Contains(e.id)) issues.Add(Warn(pid, $"requires badge '{e.id}', which is not one of the title's badges."));
                        if (e.kind == FlagRef && !flagsSet.Contains(e.id)) issues.Add(Warn(pid, $"requires flag '{e.id}', which no playlist sets (only the game's Lua can unlock it)."));
                    }
                }
                foreach (string f in SetFlags(p))
                    if (!FlagId.IsMatch(f.Split('=')[0])) issues.Add(Err(pid, $"sets flag '{f}': flag names are letters, digits, _ . : -"));
                if (badgeIds != null)
                    foreach (string b in SetBadges(p)) if (!badgeIds.Contains(b)) issues.Add(Warn(pid, $"sets badge '{b}', which is not one of the title's badges."));
            }
            foreach (string id in Cycle(list, titleId)) issues.Add(Err(id, "its requires form a loop with other playlists (none of them could ever unlock)."));
            if (list.All(p => !IsStarter(p))) issues.Add(Err(null, "every playlist is locked (requires): leave at least one always open as the starter."));
            else if (!IsStarter(list[0])) issues.Add(Warn(list[0].id, "the headline (first playlist) is locked: new players start on another one."));
            return issues;
        }

        /// The playlists a playlist DIRECTLY requires (top-level entries of this title; {min, of} does not count:
        /// one of its alternatives can still open it).
        static List<string> Deps(PackagePlaylist p, string titleId)
            => Requires(p).Where(e => e.kind == PlaylistRef && (e.Title == null || e.Title == titleId))
                .Select(e => e.LocalId).Where(i => i != p.id).ToList();

        static IEnumerable<string> Cycle(List<PackagePlaylist> list, string titleId)
        {
            var deps = list.Where(p => !string.IsNullOrEmpty(p.id)).GroupBy(p => p.id).ToDictionary(g => g.Key, g => Deps(g.First(), titleId));
            var state = new Dictionary<string, int>();
            var onCycle = new HashSet<string>();
            var stack = new List<string>();
            void Visit(string id)
            {
                if (!deps.ContainsKey(id)) return;
                if (state.TryGetValue(id, out int s)) { if (s == 1) foreach (string x in stack.Skip(stack.IndexOf(id))) onCycle.Add(x); return; }
                state[id] = 1; stack.Add(id);
                foreach (string d in deps[id]) Visit(d);
                stack.RemoveAt(stack.Count - 1); state[id] = 2;
            }
            foreach (string id in deps.Keys) Visit(id);
            return onCycle.OrderBy(x => x, StringComparer.Ordinal);
        }

        // ── the campaign map (pure layout) ──

        public sealed class MapNode
        {
            public PackagePlaylist playlist;
            public int column, row;
            public bool starter;
            public List<string> lockedBy = new List<string>();
        }

        public sealed class MapEdge { public string from, to; public bool unlock; }   // unlock = requires; else continueWith

        /// Columns by depth: a playlist sits one column right of the deepest playlist it requires (also inside
        /// {min, of}) or follows (continueWith); nothing before it = column 0. Rows in authored order; loops ignored.
        public static List<MapNode> Map(ContentPlaylistStore.Data data, out List<MapEdge> edges)
        {
            edges = new List<MapEdge>();
            var list = (data?.playlists ?? new List<PackagePlaylist>()).Where(p => !string.IsNullOrEmpty(p.id)).GroupBy(p => p.id).Select(g => g.First()).ToList();
            var index = list.Select((p, i) => (p, i)).ToDictionary(x => x.p.id, x => x.i);
            var parents = list.ToDictionary(p => p.id, p => new List<string>());
            foreach (PackagePlaylist p in list)
            {
                foreach (Entry e in Requires(p).SelectMany(Flatten).Where(e => e.kind == PlaylistRef && e.Title == null))
                    if (index.ContainsKey(e.LocalId) && e.LocalId != p.id && !edges.Any(x => x.unlock && x.from == e.LocalId && x.to == p.id))
                    {
                        if (!parents[p.id].Contains(e.LocalId)) parents[p.id].Add(e.LocalId);
                        edges.Add(new MapEdge { from = e.LocalId, to = p.id, unlock = true });
                    }
                foreach (string next in p.continueWith)
                    if (index.ContainsKey(next) && next != p.id)
                    {
                        edges.Add(new MapEdge { from = p.id, to = next, unlock = false });
                        if (!parents[next].Contains(p.id)) parents[next].Add(p.id);
                    }
            }
            var depth = new Dictionary<string, int>();
            int Depth(string id, HashSet<string> path)
            {
                if (depth.TryGetValue(id, out int d)) return d;
                if (!path.Add(id)) return -1;             // a loop: that edge does not push the column
                int best = 0;
                foreach (string parent in parents[id])
                {
                    int pd = Depth(parent, path);
                    if (pd >= 0) best = Math.Max(best, pd + 1);
                }
                path.Remove(id);
                depth[id] = best;
                return best;
            }
            var nodes = new List<MapNode>();
            foreach (PackagePlaylist p in list)
                nodes.Add(new MapNode { playlist = p, column = Depth(p.id, new HashSet<string>()), starter = IsStarter(p), lockedBy = Requires(p).Select(Encode).ToList() });
            foreach (var col in nodes.GroupBy(n => n.column))
            {
                int row = 0;
                foreach (MapNode n in col.OrderBy(n => index[n.playlist.id])) n.row = row++;
            }
            return nodes;
        }

        /// Plain words for a gate: "after Meadow, with badge Super Adventurer, 2 of (…)".
        public static string Describe(PackagePlaylist p, Func<string, string> playlistName = null, Func<string, string> badgeName = null)
            => IsStarter(p) ? "always open" : string.Join(", ", Requires(p).Select(e => Words(e, playlistName, badgeName)));

        public static string Words(Entry e, Func<string, string> playlistName = null, Func<string, string> badgeName = null)
        {
            if (e == null) return null;
            switch (e.kind)
            {
                case PlaylistRef: return "after " + (e.Title == null ? playlistName?.Invoke(e.LocalId) ?? e.LocalId : e.Title + " " + e.LocalId);
                case BadgeRef: return "with badge " + (badgeName?.Invoke(e.id) ?? e.id);
                case FlagRef: return "when " + e.id + (string.IsNullOrEmpty(e.value) ? "" : " is " + e.value);
                default: return $"{e.min} of ({string.Join(" / ", e.of.Select(c => Words(c, playlistName, badgeName)))})";
            }
        }

        static PlaylistIssue Err(string id, string message) => new PlaylistIssue { playlistId = id, isError = true, message = message };
        static PlaylistIssue Warn(string id, string message) => new PlaylistIssue { playlistId = id, isError = false, message = message };
    }
}
#endif
