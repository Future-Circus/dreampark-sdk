#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// A title's authored playlists (Docs/PLAYLISTS.md): the committed
    /// Assets/Content/{id}/.dreampark-playlists.json. Hand-edited for now; the
    /// Content Uploader's Playlists tab lists and validates it, and the package
    /// compiler ships the valid ones in the manifest.
    /// </summary>
    public static class ContentPlaylistStore
    {
        public const int CurrentVersion = PackagePlaylist.CurrentSchemaVersion;

        [Serializable]
        public sealed class Data
        {
            public int schemaVersion = CurrentVersion;
            public List<PlaylistKind> kinds = new List<PlaylistKind>();
            public List<PackagePlaylist> playlists = new List<PackagePlaylist>();
            /// § 15.1 title-level `campaigns` [{id, name, steps}] and `flags` [{id, hint, personal, once, values}], as
            /// JSON array TEXT ("" = none; steps mix ids, arrays and {min, of}, which JsonUtility cannot read).
            public string campaignsJson = "";
            public string flagsJson = "";
            /// § 6.0a "" | "tour" | "club": how the title's playlists use the space (club = one stable layout).
            public string layout = "";
        }

        public static string AssetPath(string contentId)
            => $"Assets/Content/{contentId}/.dreampark-playlists.json";

        public static string FullPath(string contentId)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", AssetPath(contentId)));

        public static bool Exists(string contentId)
            => !string.IsNullOrEmpty(contentId) && File.Exists(FullPath(contentId));

        /// <summary>Null when the title has no playlists file. Throws on a broken or newer file.</summary>
        public static Data Load(string contentId)
            => Exists(contentId) ? Deserialize(File.ReadAllText(FullPath(contentId))) : null;

        public static Data Deserialize(string json)
        {
            Data data;
            try { data = JsonUtility.FromJson<Data>(AuthoringAsText(json)); }
            catch (Exception e) { throw new InvalidOperationException("Playlists file is not valid JSON: " + e.Message); }
            if (data == null) throw new InvalidOperationException("Playlists file is empty.");
            if (data.schemaVersion < 1)
                throw new InvalidOperationException("Playlists file has no schemaVersion (expected 1).");
            if (data.schemaVersion > CurrentVersion)
                throw new InvalidOperationException(
                    $"Playlists file is schema v{data.schemaVersion}; this SDK reads up to v{CurrentVersion}.");
            data.kinds = (data.kinds ?? new List<PlaylistKind>()).Where(k => k != null).ToList();
            data.playlists = (data.playlists ?? new List<PackagePlaylist>()).Where(p => p != null).ToList();
            foreach (PackagePlaylist playlist in data.playlists) Normalize(playlist);
            return data;
        }

        /// Stop `options` are authored as JSON OBJECTS but carried as text (PlaylistStop.options), which
        /// JsonUtility cannot read from an object: rewrite each one as its JSON text first. A non-object
        /// keeps its JSON text too, so the validator can report it. Files without options are untouched.
        internal static string StopOptionsAsText(string json) => AuthoringAsText(json);

        /// § 15.1 gate fields whose authored shapes JsonUtility cannot read (objects / mixed arrays).
        static readonly string[] GateFields = { "requires", "sets", "window", "personal" };

        /// Stop `options` and the playlist gates (`requires`, `sets`, `window`, `personal`) are authored as JSON
        /// objects / mixed arrays but carried as TEXT: options → `PlaylistStop.options`; gates → `requiresJson` …
        /// (full fidelity) plus the Studio's string projection in `requires` / `sets`. Files without them are untouched.
        internal static string AuthoringAsText(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            json = RepairFieldsAsText(json);
            bool any = json.IndexOf("\"options\"", StringComparison.Ordinal) >= 0
                || GateFields.Any(f => json.IndexOf("\"" + f + "\"", StringComparison.Ordinal) >= 0)
                || json.IndexOf("\"campaigns\"", StringComparison.Ordinal) >= 0;
            if (!any) return json;
            var root0 = new Defective.JSON.JSONObject(json);
            if (root0.type != Defective.JSON.JSONObject.Type.Object) return json;
            bool gatesChanged = false;
            // Title-level campaigns / flags (§ 15.1) → text.
            foreach (string f in new[] { "campaigns", "flags" })
            {
                Defective.JSON.JSONObject v = root0.GetField(f);
                if (v == null) continue;
                root0.RemoveField(f);
                root0.SetField(f + "Json", v.type == Defective.JSON.JSONObject.Type.Array ? v.Print() : "");
                gatesChanged = true;
            }
            foreach (Defective.JSON.JSONObject p in root0.GetField("playlists")?.list ?? new List<Defective.JSON.JSONObject>())
                if (p != null && p.type == Defective.JSON.JSONObject.Type.Object) gatesChanged |= GatesAsText(p);
            json = gatesChanged ? root0.Print() : json;
            if (json.IndexOf("\"options\"", StringComparison.Ordinal) < 0) return json;
            var root = new Defective.JSON.JSONObject(json);
            if (root.type != Defective.JSON.JSONObject.Type.Object) return json;
            Defective.JSON.JSONObject playlists = root.GetField("playlists");
            if (playlists?.list == null) return json;
            bool changed = false;
            foreach (Defective.JSON.JSONObject p in playlists.list)
                foreach (Defective.JSON.JSONObject stop in StopsOf(p))
                    {
                        Defective.JSON.JSONObject o = stop?.GetField("options");
                        if (o == null || o.type == Defective.JSON.JSONObject.Type.String) continue;
                        stop.SetField("options", o.type == Defective.JSON.JSONObject.Type.Null ? "" : o.Print());
                        changed = true;
                    }
            return changed ? root.Print() : json;
        }

        // repair-oct20 (pl-runtime, CONTRACT § 2–§ 5): the playlist's behaviour + presentation objects JsonUtility cannot
        // read become TEXT (`lua` is already text); the compiler adds each authored object VERBATIM (`sourceJson`,
        // SourceObjects) so nothing the SDK sends is dropped: playlist `dreamie` / `timing` / `story` / `layout` / `campaign` → *Json,
        // round `scene` / `choice` → *Json, stop `goal` ({lua} → the Lua text, "default" stays).
        static readonly string[] PlaylistObjectFields = { "dreamie", "timing", "story", "layout", "campaign" };
        static readonly string[] LegObjectFields = { "scene", "choice", "dreamie" };

        internal static string RepairFieldsAsText(string json)
        {
            var root = new Defective.JSON.JSONObject(json);
            if (root.type != Defective.JSON.JSONObject.Type.Object) return json;
            var playlists = root.GetField("playlists");
            if (playlists?.list == null) return json;
            bool changed = false;
            foreach (Defective.JSON.JSONObject p in playlists.list)
            {
                if (p == null || p.type != Defective.JSON.JSONObject.Type.Object) continue;
                // CONTRACT § 8: a v1 record is upgraded on read (minutes → estMinutes, a derivable objective dropped,
                // layout beats / react.at colocated with their round / stop); then the v2 fields map onto the model.
                changed |= PlaylistRecordV2.Upgrade(p);
                foreach (Defective.JSON.JSONObject leg in p.GetField("arc")?.list ?? new List<Defective.JSON.JSONObject>())
                {
                    var est = leg?.type == Defective.JSON.JSONObject.Type.Object ? leg.GetField("estMinutes") : null;
                    if (est == null || est.type != Defective.JSON.JSONObject.Type.Number) continue;
                    double m = est.doubleValue;
                    leg.SetField("minutes", m > 0 ? Math.Max(1, (int)Math.Round(m)) : 0);   // the model's (and older apps') field
                    changed = true;
                }
                foreach (string f in PlaylistObjectFields)
                {
                    var v = p.GetField(f);
                    if (v == null) continue;
                    p.RemoveField(f);
                    if (v.type == Defective.JSON.JSONObject.Type.Object || v.type == Defective.JSON.JSONObject.Type.Array) p.SetField(f + "Json", v.Print());
                    else if (v.type == Defective.JSON.JSONObject.Type.String) p.SetField(f + "Json", Defective.JSON.JSONObject.CreateStringObject(v.stringValue).Print());
                    changed = true;
                }
                var rwj = p.GetField("roundWord");   // a bare "Race" → {one: "Race"} (JsonUtility reads objects only)
                if (rwj != null && rwj.type == Defective.JSON.JSONObject.Type.String)
                {
                    var o = new Defective.JSON.JSONObject(); o.AddField("one", rwj.stringValue ?? ""); o.AddField("many", "");
                    p.SetField("roundWord", o); changed = true;
                }
                var rev = p.GetField("rev");
                if (rev != null && rev.type == Defective.JSON.JSONObject.Type.Number) { p.SetField("rev", rev.Print()); changed = true; }
                foreach (Defective.JSON.JSONObject leg in p.GetField("arc")?.list ?? new List<Defective.JSON.JSONObject>())
                {
                    if (leg == null || leg.type != Defective.JSON.JSONObject.Type.Object) continue;
                    foreach (string f in LegObjectFields)
                    {
                        var v = leg.GetField(f);
                        if (v == null) continue;
                        leg.RemoveField(f);
                        leg.SetField(f + "Json", v.type == Defective.JSON.JSONObject.Type.Null ? "" : v.Print());
                        changed = true;
                    }
                }
                // pl-sets: a sets record's POOL entries carry the same per-stop objects as arc stops.
                foreach (Defective.JSON.JSONObject stop in StopsOf(p))
                    {
                        var sd = stop?.type == Defective.JSON.JSONObject.Type.Object ? stop.GetField("dreamie") : null;
                        if (sd != null)
                        {
                            stop.RemoveField("dreamie");
                            stop.SetField("dreamieJson", sd.type == Defective.JSON.JSONObject.Type.Object ? sd.Print() : "");
                            changed = true;
                        }
                        var g = stop?.GetField("goal");
                        if (g == null || g.type == Defective.JSON.JSONObject.Type.String) continue;
                        string lua = g.type == Defective.JSON.JSONObject.Type.Object ? g.GetField("lua")?.stringValue : null;
                        stop.SetField("goal", lua ?? "");
                        changed = true;
                    }
            }
            return changed ? root.Print() : json;
        }

        /// Every stop object of a playlist: its arc's stops, then its pool (pl-sets).
        static IEnumerable<Defective.JSON.JSONObject> StopsOf(Defective.JSON.JSONObject p)
        {
            foreach (Defective.JSON.JSONObject leg in p?.GetField("arc")?.list ?? new List<Defective.JSON.JSONObject>())
                foreach (Defective.JSON.JSONObject stop in leg?.GetField("stops")?.list ?? new List<Defective.JSON.JSONObject>())
                    yield return stop;
            foreach (Defective.JSON.JSONObject stop in p?.GetField("pool")?.list ?? new List<Defective.JSON.JSONObject>())
                yield return stop;
        }

        /// Each playlist's authored object verbatim, by id (the compiler's `sourceJson` pass-through).
        public static Dictionary<string, string> SourceObjects(string contentId)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Exists(contentId)) return d;
            var root = new Defective.JSON.JSONObject(File.ReadAllText(FullPath(contentId)));
            foreach (Defective.JSON.JSONObject p in root.GetField("playlists")?.list ?? new List<Defective.JSON.JSONObject>())
            {
                string id = p?.GetField("id")?.stringValue;
                if (string.IsNullOrEmpty(id)) continue;
                PlaylistRecordV2.Upgrade(p);   // every writer emits v2 (CONTRACT § 8): the manifest's verbatim copy too
                d[id] = p.Print();
            }
            return d;
        }

        /// The title's default goal blocks: `Assets/Content/<Title>/.dreampark-goals/<attractionId>.<kind>.lua` → a JSON
        /// object {"<attractionId>.<kind>": "<lua>"} ("" = none). Hidden folder on purpose: Unity never imports it.
        public static string GoalsDir(string contentId) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", $"Assets/Content/{contentId}/.dreampark-goals"));

        public static string GoalsJson(string contentId)
        {
            string dir = GoalsDir(contentId);
            if (!Directory.Exists(dir)) return "";
            var o = new Defective.JSON.JSONObject();
            foreach (string f in Directory.GetFiles(dir, "*.lua").OrderBy(x => x, StringComparer.Ordinal))
            {
                string key = Path.GetFileNameWithoutExtension(f);   // "A_LavaPits.jump"
                if (key.IndexOf('.') <= 0) continue;
                string lua = File.ReadAllText(f);
                if (!string.IsNullOrWhiteSpace(lua)) o.AddField(key, lua);
            }
            return o.count > 0 ? o.Print() : "";
        }

        /// One playlist's gates → `*Json` text + the Studio projection. True when anything changed.
        static bool GatesAsText(Defective.JSON.JSONObject p)
        {
            bool changed = false;
            foreach (string f in GateFields)
            {
                Defective.JSON.JSONObject v = p.GetField(f);
                if (v == null) continue;
                p.SetField(f + "Json", v.type == Defective.JSON.JSONObject.Type.Null ? "" : v.Print());
                changed = true;
                if (f == "window" || f == "personal") { p.RemoveField(f); continue; }
                // The Studio's view: requires = "playlist:/badge:/flag:" refs; sets = flag ids.
                var proj = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                if (f == "requires" && v.list != null)
                    foreach (Defective.JSON.JSONObject e in v.list)
                    {
                        if (e.type == Defective.JSON.JSONObject.Type.String)
                        {
                            string t = e.stringValue ?? "";
                            proj.Add(t.StartsWith("playlist:") || t.StartsWith("badge:") || t.StartsWith("flag:") ? t : "playlist:" + t);
                        }
                        else if (e.type == Defective.JSON.JSONObject.Type.Object && e.count == 1)
                        {
                            if (e.GetField("badge")?.stringValue is string b) proj.Add("badge:" + b);
                            else if (e.GetField("flag")?.stringValue is string fl) proj.Add("flag:" + fl);
                            else if (e.GetField("playlist")?.stringValue is string pl) proj.Add("playlist:" + pl);
                        }
                    }
                else if (f == "sets" && v.type == Defective.JSON.JSONObject.Type.Object)
                {
                    Defective.JSON.JSONObject flags = v.GetField("flags");
                    if (flags?.keys != null)
                        for (int i = 0; i < flags.keys.Count; i++) proj.Add(flags.keys[i]);
                }
                else if (f == "sets" && v.type == Defective.JSON.JSONObject.Type.Array) { p.SetField(f + "Json", ""); continue; }
                p.SetField(f, proj);
            }
            return changed;
        }

        public static List<PlaylistKind> KindsOrDefault(Data data)
            => data != null && data.kinds != null && data.kinds.Count > 0
                ? data.kinds : new List<PlaylistKind> { PlaylistKind.Default() };

        // JsonUtility leaves missing nested objects/lists null in some Unity
        // versions; make every reader's life simple.
        static List<string> CleanList(List<string> values)
            => (values ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
                .Distinct(StringComparer.Ordinal).ToList();

        internal static void Normalize(PackagePlaylist playlist)
        {
            playlist.tags = (playlist.tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();
            playlist.badges = (playlist.badges ?? new List<string>()).Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.Trim()).Distinct().ToList();
            // Chapters & unlocks (PLAYLISTS.md § 2.1, proposal): never null, trimmed, no repeats.
            playlist.continueWith = CleanList(playlist.continueWith);
            playlist.requires = CleanList(playlist.requires);
            playlist.sets = CleanList(playlist.sets);
            playlist.chapter = (playlist.chapter ?? "").Trim();
            playlist.requiresJson = (playlist.requiresJson ?? "").Trim();
            playlist.setsJson = (playlist.setsJson ?? "").Trim();
            playlist.windowJson = (playlist.windowJson ?? "").Trim();
            playlist.personalJson = (playlist.personalJson ?? "").Trim();
            playlist.fit = playlist.fit ?? new PlaylistFit();
            // § 8 v2: schema, requiresRelease, play {mode, players}; fit.minPlayers mirrors play's minimum (older apps).
            playlist.schema = PlaylistRecordV2.Schema;
            playlist.requiresRelease = Math.Max(0, playlist.requiresRelease);
            playlist.play = playlist.play ?? new PlaylistPlay();
            playlist.play.mode = (playlist.play.mode ?? "").Trim().ToLowerInvariant();
            playlist.play.players = (playlist.play.players ?? new List<int>()).Take(2).ToList();
            if (playlist.play.Min > 0) playlist.fit.minPlayers = playlist.play.Min;
            else if (playlist.fit.minPlayers > 0 && playlist.play.players.Count == 0)
                playlist.play.players = new List<int> { playlist.fit.minPlayers, Math.Max(playlist.fit.minPlayers, PlaylistRecordV2.MaxPlayers) };
            playlist.rules = playlist.rules ?? new PlaylistRules();
            if (playlist.rules.scoring == null) playlist.rules.scoring = "";
            if (string.IsNullOrEmpty(playlist.rules.advance)) playlist.rules.advance = "on_complete";
            if (playlist.rules.script == null) playlist.rules.script = "";
            if (string.IsNullOrWhiteSpace(playlist.owner)) playlist.owner = "title";
            playlist.arc = (playlist.arc ?? new List<PlaylistLeg>()).Where(l => l != null).ToList();
            // pl-sets: the attraction pool of a sets record (each with its challenge + options), cleaned like arc stops.
            playlist.pool = (playlist.pool ?? new List<PlaylistStop>()).Where(x => x != null).ToList();
            foreach (PlaylistStop stop in playlist.pool)
            {
                NormalizeStop(stop);
                if (string.IsNullOrWhiteSpace(stop.role)) stop.role = "stage";   // a pool attraction is a stage unless it says boss
            }
            for (int i = 0; i < playlist.arc.Count; i++)
            {
                PlaylistLeg leg = playlist.arc[i];
                if (string.IsNullOrWhiteSpace(leg.id)) leg.id = "leg" + (i + 1);
                if (string.IsNullOrWhiteSpace(leg.next)) leg.next = "walk_on";
                leg.objectiveText = (leg.objectiveText ?? "").Trim();
                leg.bonusLevels = CleanList(leg.bonusLevels);
                leg.titles = (leg.titles ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim()).Distinct(StringComparer.Ordinal).ToList();
                leg.dreamieJson = (leg.dreamieJson ?? "").Trim();
                leg.stops = (leg.stops ?? new List<PlaylistStop>()).Where(s => s != null).ToList();
                // § 8.7: a v2 round has no objective — derive it (the runner and the release path still need one).
                if (leg.objective == null || string.IsNullOrEmpty(leg.objective.type)) leg.objective = PlaylistRecordV2.DeriveObjective(leg);
                foreach (PlaylistStop stop in leg.stops) NormalizeStop(stop);
            }
        }

        static void NormalizeStop(PlaylistStop stop)
        {
                {
                    stop.alternates = (stop.alternates ?? new List<string>())
                        .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
                    stop.placement = stop.placement ?? new PlaylistPlacement();
                    if (string.IsNullOrWhiteSpace(stop.placement.prefer)) stop.placement.prefer = "any";
                    stop.contentId = string.IsNullOrWhiteSpace(stop.contentId) ? null : stop.contentId.Trim();
                    stop.options = string.IsNullOrWhiteSpace(stop.options) || stop.options.Trim() == "{}" ? "" : stop.options.Trim();
                    stop.objectiveText = (stop.objectiveText ?? "").Trim();
                    stop.displayName = (stop.displayName ?? "").Trim();
                    stop.challenge = (stop.challenge ?? "").Trim();
                    stop.bonus = CleanList(stop.bonus);
                    stop.introHint = (stop.introHint ?? "").Trim();
                    stop.id = (stop.id ?? "").Trim();
                    stop.lockedUntil = CleanList(stop.lockedUntil);
                    stop.lockSign = (stop.lockSign ?? "").Trim();
                    stop.dreamieJson = (stop.dreamieJson ?? "").Trim();
                    stop.alternateAddresses = new List<string>();
                    stop.alternateAttractionIds = new List<string>();
                    stop.alternateResourceNames = new List<string>();
                    stop.alternateOccurrenceIds = new List<string>();
                }
            }
    }

    /// <summary>One placeable attraction prefab a playlist stop can name.</summary>
    public sealed class PlaylistAttractionRef
    {
        public string guid;
        public string assetPath;
        public string leaf;              // prefab file name, e.g. "A_Cannonsnout"
        public string resourceName;      // Web catalog key: asset path stem without "Assets/"
        public string size;              // AttractionTemplate.size, for the runtime address
        public bool hasPackingBake;

        public string AddressFor(string targetContentId) => $"{targetContentId}/Levels/{size}/{leaf}";
    }

    public sealed class PlaylistIssue
    {
        public string playlistId;        // null = file-level
        public bool isError;
        public string message;
        public override string ToString() => (isError ? "error: " : "warning: ")
            + (string.IsNullOrEmpty(playlistId) ? "" : playlistId + ": ") + message;
    }

    /// <summary>
    /// The rules of a COMPLETE playlist (Docs/PLAYLISTS.md § 3). Pure: the
    /// attraction lookup is injected, so the rules are unit-testable without assets.
    /// </summary>
    public static class ContentPlaylistValidator
    {
        static readonly Regex IdPattern = new Regex("^[a-z0-9][a-z0-9_-]{0,63}$");
        static readonly Regex BadgePattern = new Regex("^[A-Za-z0-9_.:-]{1,80}$");
        const int MaxPlaylists = 50, MaxLegs = 12, MaxStopsPerLeg = 12;

        /// <summary>Every placeable attraction prefab of the title, by file name and by resourceName.</summary>
        public static Func<string, IReadOnlyList<PlaylistAttractionRef>> AssetResolver(string contentId)
        {
            string root = $"Assets/Content/{contentId}";
            var all = new List<PlaylistAttractionRef>();
            if (AssetDatabase.IsValidFolder(root))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)
                        || path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0
                        || DreamSequenceGenerator.IsSpecialLevelPath(contentId, path)
                        || DreamPark.WaysToPlay.RunnerIds.IsRunnerOnlyAsset(path)) continue;
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    AttractionTemplate attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                    if (attraction == null || attraction is DreamSequenceTemplate) continue;
                    string stem = path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        ? path.Substring("Assets/".Length) : path;
                    int dot = stem.LastIndexOf('.');
                    if (dot >= 0) stem = stem.Substring(0, dot);
                    all.Add(new PlaylistAttractionRef
                    {
                        guid = guid,
                        assetPath = path,
                        leaf = Path.GetFileNameWithoutExtension(path),
                        resourceName = stem,
                        size = attraction.size.ToString(),
                        hasPackingBake = attraction.HasPackingBake,
                    });
                }
            }
            return Resolver(all);
        }

        public static Func<string, IReadOnlyList<PlaylistAttractionRef>> Resolver(IEnumerable<PlaylistAttractionRef> all)
        {
            var list = all.ToList();
            return reference =>
            {
                if (string.IsNullOrWhiteSpace(reference)) return Array.Empty<PlaylistAttractionRef>();
                string r = reference.Trim();
                var exact = list.Where(a => string.Equals(a.resourceName, r, StringComparison.Ordinal)).ToList();
                if (exact.Count > 0) return exact;
                return list.Where(a => string.Equals(a.leaf, r, StringComparison.Ordinal)).ToList();
            };
        }

        /// <param name="titleId">The title whose file this is (its own contentId).</param>
        public static List<PlaylistIssue> Validate(ContentPlaylistStore.Data data, string titleId,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve)
        {
            var issues = new List<PlaylistIssue>();
            if (data == null) return issues;
            List<PlaylistKind> kinds = ContentPlaylistStore.KindsOrDefault(data);
            var kindIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlaylistKind kind in kinds)
            {
                if (string.IsNullOrEmpty(kind.id) || !IdPattern.IsMatch(kind.id))
                    issues.Add(Err(null, $"kind id '{kind.id}' must be lower-case letters, digits, - or _."));
                else if (!kindIds.Add(kind.id))
                    issues.Add(Err(null, $"kind '{kind.id}' is declared twice."));
                if (string.IsNullOrWhiteSpace(kind.singular) || string.IsNullOrWhiteSpace(kind.plural))
                    issues.Add(Err(null, $"kind '{kind.id}' needs both a singular and a plural word."));
            }
            if (data.playlists.Count == 0) issues.Add(Warn(null, "the file declares no playlists."));
            if (data.playlists.Count > MaxPlaylists) issues.Add(Err(null, $"more than {MaxPlaylists} playlists."));

            var ids = new HashSet<string>(StringComparer.Ordinal);
            // The title's bosses: every boss / miniboss stop of any of its playlists (bonusLevels refuses them).
            var bosses = data.playlists.Where(p => p?.arc != null).SelectMany(p => p.arc).Where(l => l?.stops != null)
                .SelectMany(l => l.stops).Where(s => s != null && (s.role == "boss" || s.role == "miniboss"))
                .Select(s => s.attraction).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).ToList();
            foreach (PackagePlaylist p in data.playlists)
            {
                string pid = string.IsNullOrEmpty(p.id) ? "(no id)" : p.id;
                if (string.IsNullOrEmpty(p.id) || !IdPattern.IsMatch(p.id))
                    issues.Add(Err(pid, "id must be 1–64 lower-case letters, digits, - or _ (and never change)."));
                else if (!ids.Add(p.id)) issues.Add(Err(pid, "id is used by another playlist."));
                ValidatePlaylist(p, pid, titleId, kindIds, resolve, issues, bosses);
            }
            foreach (PackagePlaylist p in data.playlists) CleanContinueWith(p, titleId, ids, issues);
            foreach (PackagePlaylist p in data.playlists) CleanGates(p, issues);
            CleanCampaigns(data, ids, issues);
            return issues;
        }

        public const int MaxContinueWith = 3;

        /// § 15.1 title `campaigns` / `flags`: WARNINGS only, cleaned in place. A campaign needs an id, a name and
        /// steps; a step is a playlist id of this title, an array of them (a parallel set) or {min, of}. Unknown ids
        /// and bad steps are dropped. A flag needs an id ([A-Za-z0-9._:-]); `hint`, `personal`, `once`, `values` kept.
        internal static void CleanCampaigns(ContentPlaylistStore.Data data, HashSet<string> ids, List<PlaylistIssue> issues)
        {
            Defective.JSON.JSONObject Arr(string text)
            {
                string t = (text ?? "").Trim();
                if (t.Length == 0 || t == "[]") return null;
                try { var v = new Defective.JSON.JSONObject(t); return v.type == Defective.JSON.JSONObject.Type.Array ? v : null; }
                catch (Exception) { return null; }
            }
            bool IdOk(Defective.JSON.JSONObject s) => s != null && s.type == Defective.JSON.JSONObject.Type.String && ids.Contains(s.stringValue ?? "");
            var campaigns = Arr(data.campaignsJson);
            var keptC = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
            foreach (Defective.JSON.JSONObject c in campaigns?.list ?? new List<Defective.JSON.JSONObject>())
            {
                string cid = c?.GetField("id")?.stringValue;
                if (string.IsNullOrEmpty(cid) || !IdPattern.IsMatch(cid) || c.GetField("steps")?.list == null)
                { issues.Add(Warn(null, $"campaign {c?.Print()} needs an id and steps; dropped.")); continue; }
                var steps = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                foreach (Defective.JSON.JSONObject st in c.GetField("steps").list)
                {
                    if (IdOk(st)) { steps.Add(st); continue; }
                    if (st.type == Defective.JSON.JSONObject.Type.Array && st.list.Count > 0 && st.list.All(IdOk)) { steps.Add(st); continue; }
                    var of = st.GetField("of");
                    if (st.type == Defective.JSON.JSONObject.Type.Object && of?.list != null && of.list.All(IdOk)
                        && st.GetField("min") is Defective.JSON.JSONObject mn && mn.isInteger && mn.longValue >= 1 && mn.longValue <= of.list.Count)
                    { steps.Add(st); continue; }
                    issues.Add(Warn(null, $"campaign '{cid}' step {st.Print()} is not a playlist of this title (or a set / {{min, of}} of them); dropped."));
                }
                if (steps.count == 0) { issues.Add(Warn(null, $"campaign '{cid}' has no valid steps; dropped.")); continue; }
                var keep = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Object);
                keep.AddField("id", cid);
                if (c.GetField("name")?.stringValue is string nm) keep.AddField("name", nm);
                keep.AddField("steps", steps);
                keptC.Add(keep);
            }
            data.campaignsJson = keptC.count > 0 ? keptC.Print() : "";
            var flags = Arr(data.flagsJson);
            var keptF = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
            foreach (Defective.JSON.JSONObject f in flags?.list ?? new List<Defective.JSON.JSONObject>())
            {
                string fid = f?.GetField("id")?.stringValue;
                if (string.IsNullOrEmpty(fid) || !FlagIdRe.IsMatch(fid)) { issues.Add(Warn(null, $"flag {f?.Print()} needs an id; dropped.")); continue; }
                keptF.Add(f);
            }
            data.flagsJson = keptF.count > 0 ? keptF.Print() : "";
        }

        // ── § 15.1 gates: WARNINGS only — an entry of an unknown shape is dropped (Web `normalizeGates` mirrors it) ──
        const int MaxGateEntries = 16;
        static readonly Regex GateRef = new Regex("^[A-Za-z0-9_.:/-]{1,200}$");
        static readonly Regex FlagIdRe = new Regex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$");
        static readonly string[] GateKeys = { "badge", "flag", "is", "playlist", "min", "of" };

        /// A `requires` / `personal` entry: "id" | "T/id" | "title:T:id" | "playlist:/badge:/flag:" ref, or an object
        /// {badge} / {flag} / {flag, is} / {playlist} / {min, of} (several keys = all of them).
        internal static bool GateEntryOk(Defective.JSON.JSONObject e, int depth = 0)
        {
            if (e == null || depth > 3) return false;
            if (e.type == Defective.JSON.JSONObject.Type.String) return GateRef.IsMatch(e.stringValue ?? "");
            if (e.type != Defective.JSON.JSONObject.Type.Object || e.keys == null || e.keys.Count == 0) return false;
            if (e.keys.Any(k => Array.IndexOf(GateKeys, k) < 0)) return false;
            Defective.JSON.JSONObject b = e.GetField("badge"), f = e.GetField("flag"), pl = e.GetField("playlist"),
                min = e.GetField("min"), of = e.GetField("of"), isV = e.GetField("is");
            if (b != null && (b.type != Defective.JSON.JSONObject.Type.String || !BadgePattern.IsMatch(b.stringValue ?? ""))) return false;
            if (f != null && (f.type != Defective.JSON.JSONObject.Type.String || !FlagIdRe.IsMatch(f.stringValue ?? ""))) return false;
            if (isV != null && (f == null || isV.type == Defective.JSON.JSONObject.Type.Object || isV.type == Defective.JSON.JSONObject.Type.Array)) return false;
            if (pl != null && (pl.type != Defective.JSON.JSONObject.Type.String || !GateRef.IsMatch(pl.stringValue ?? ""))) return false;
            if (min != null || of != null)
            {
                if (min == null || of?.list == null || !min.isInteger) return false;
                long n = min.longValue;
                if (n < 1 || n > of.list.Count || of.list.Count > MaxGateEntries) return false;
                if (of.list.Any(x => !GateEntryOk(x, depth + 1))) return false;
            }
            return true;
        }

        /// THE CANONICAL WRITTEN FORM (§ 2.1): "kind:id" shorthand → `"<id>"` (a playlist) / `{"badge": id}` /
        /// `{"flag": id}`; every other valid entry is kept as written. Applied recursively inside `{min, of}`.
        internal static Defective.JSON.JSONObject CanonicalGate(Defective.JSON.JSONObject e)
        {
            if (e == null) return null;
            if (e.type == Defective.JSON.JSONObject.Type.String)
            {
                string t = (e.stringValue ?? "").Trim();
                int colon = t.IndexOf(':');
                string kind = colon > 0 ? t.Substring(0, colon) : "";
                if (kind == "playlist") return Defective.JSON.JSONObject.CreateStringObject(t.Substring(colon + 1));
                if (kind == "badge" || kind == "flag")
                {
                    var o = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Object);
                    o.AddField(kind, t.Substring(colon + 1));
                    return o;
                }
                return Defective.JSON.JSONObject.CreateStringObject(t);
            }
            if (e.type == Defective.JSON.JSONObject.Type.Object && e.GetField("of")?.list is List<Defective.JSON.JSONObject> of)
            {
                var copy = e.Copy();
                var arr = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                foreach (var x in of) arr.Add(CanonicalGate(x));
                copy.SetField("of", arr);
                return copy;
            }
            return e;
        }

        /// Cleans `requiresJson` / `personalJson` / `setsJson` / `windowJson` in place (warning per dropped entry) and
        /// writes them in the canonical form. A file with only the Studio's `requires` / `sets` lists gets its
        /// canonical text built from them, so the manifest and the upload always carry § 15 shapes.
        internal static void CleanGates(PackagePlaylist p, List<PlaylistIssue> issues)
        {
            string pid = string.IsNullOrEmpty(p.id) ? "(no id)" : p.id;
            if (string.IsNullOrEmpty(p.requiresJson) && p.requires != null && p.requires.Count > 0)
            {
                var arr = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                foreach (string r in p.requires) arr.Add(r);
                p.requiresJson = arr.Print();
            }
            if (string.IsNullOrEmpty(p.setsJson) && p.sets != null && p.sets.Count > 0)
            {
                var flags = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Object);
                foreach (string f in p.sets) flags.AddField(f.StartsWith("flag:") ? f.Substring(5) : f, true);
                var o = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Object);
                o.AddField("flags", flags);
                p.setsJson = o.Print();
            }
            // Defective.JSON throws on an empty container ("[]", "{}") and on garbage: both read as "none".
            Defective.JSON.JSONObject Parse(string text, string name)
            {
                string t = (text ?? "").Trim();
                if (t.Length == 0 || t == "[]" || t == "{}" || t == "null") return null;
                try { return new Defective.JSON.JSONObject(t); }
                catch (Exception) { issues.Add(Warn(pid, $"{name} is not JSON; dropped.")); return null; }
            }
            string CleanList(string text, string name)
            {
                var v = Parse(text, name);
                if (v == null) return "";
                if (v.type != Defective.JSON.JSONObject.Type.Array)
                { issues.Add(Warn(pid, $"{name} must be an array; dropped.")); return ""; }
                var kept = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                foreach (Defective.JSON.JSONObject e in v.list ?? new List<Defective.JSON.JSONObject>())
                {
                    if (kept.count >= MaxGateEntries) { issues.Add(Warn(pid, $"{name}: only the first {MaxGateEntries} entries are kept.")); break; }
                    if (GateEntryOk(e)) kept.Add(CanonicalGate(e));
                    else issues.Add(Warn(pid, $"{name} entry {e.Print()} is not a known gate shape; dropped."));
                }
                return kept.count > 0 ? kept.Print() : "";
            }
            // PLAYLIST-PLAN § 3.4 (pl-sets): `requires: {badges: [...]}` is the gate list [{badge}, …] (any present account holding
            // each badge opens it for the group).
            var rqo = Parse(p.requiresJson, "requires");
            if (rqo != null && rqo.type == Defective.JSON.JSONObject.Type.Object && rqo.GetField("badges") is Defective.JSON.JSONObject rb)
            {
                var arr = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                foreach (Defective.JSON.JSONObject b in rb.list ?? new List<Defective.JSON.JSONObject>())
                    if (b != null && b.type == Defective.JSON.JSONObject.Type.String && !string.IsNullOrEmpty(b.stringValue))
                    { var g = new Defective.JSON.JSONObject(); g.AddField("badge", b.stringValue); arr.Add(g); }
                p.requiresJson = arr.count > 0 ? arr.Print() : "";
            }
            p.requiresJson = CleanList(p.requiresJson, "requires");
            p.personalJson = CleanList(p.personalJson, "personal");
            if (!string.IsNullOrEmpty(p.setsJson))
            {
                var v = Parse(p.setsJson, "sets");
                if (v != null && (v.type != Defective.JSON.JSONObject.Type.Object || (v.keys ?? new List<string>()).Any(k => k != "flags" && k != "badges")))
                    issues.Add(Warn(pid, "sets must be {flags, badges}; unknown keys are dropped."));
                if (v == null || v.type != Defective.JSON.JSONObject.Type.Object) p.setsJson = "";
                else
                {
                    var keep = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Object);
                    if (v.GetField("flags") is Defective.JSON.JSONObject fl && fl.type == Defective.JSON.JSONObject.Type.Object) keep.AddField("flags", fl);
                    if (v.GetField("badges") is Defective.JSON.JSONObject bs && bs.type == Defective.JSON.JSONObject.Type.Array) keep.AddField("badges", bs);
                    p.setsJson = keep.count > 0 ? keep.Print() : "";
                }
            }
            if (!string.IsNullOrEmpty(p.windowJson))
            {
                var w = Parse(p.windowJson, "window");
                if (w == null) { p.windowJson = ""; return; }
                string from = w.GetField("from")?.stringValue, until = w.GetField("until")?.stringValue;
                bool okFrom = from == null || DateTime.TryParse(from, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime f);
                bool okUntil = until == null || DateTime.TryParse(until, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime u);
                DateTime.TryParse(from ?? "", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime fd);
                DateTime.TryParse(until ?? "", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime ud);
                if (w.type != Defective.JSON.JSONObject.Type.Object || !okFrom || !okUntil || (from == null && until == null))
                { issues.Add(Warn(pid, "window must be {from, until, label} ISO dates; dropped.")); p.windowJson = ""; }
                else if (from != null && until != null && ud <= fd)
                { issues.Add(Warn(pid, "window.until is not after from; dropped.")); p.windowJson = ""; }
            }
        }

        /// § 6.0a AUTHORED LOCKS: `lockedUntil` refs must name another stop of the SAME round (its `id` or attraction),
        /// with no cycles; a platform bookend is never locked. ERRORS (a lock that can never open breaks the round).
        internal static void CheckLocks(PlaylistLeg leg, string where, string pid, List<PlaylistIssue> issues)
        {
            int n = leg.stops.Count;
            int Find(string r, int self)
            {
                for (int j = 0; j < n; j++)
                {
                    if (j == self) continue;
                    PlaylistStop s = leg.stops[j];
                    if ((!string.IsNullOrEmpty(s.id) && string.Equals(s.id, r, StringComparison.Ordinal))
                        || string.Equals(s.attraction, r, StringComparison.OrdinalIgnoreCase)) return j;
                }
                return -1;
            }
            var edges = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                edges[i] = new List<int>();
                PlaylistStop s = leg.stops[i];
                if (s.lockedUntil == null || s.lockedUntil.Count == 0) continue;
                if (PlaylistBookends.IsPlatformStop(s.contentId, s.attraction))
                { issues.Add(Err(pid, $"{where}: platform stop '{s.attraction}' cannot be locked.")); continue; }
                foreach (string r in s.lockedUntil)
                {
                    int j = Find(r, i);
                    if (j < 0) issues.Add(Err(pid, $"{where}: stop '{s.attraction}' lockedUntil '{r}' is not another stop of this round."));
                    else edges[i].Add(j);
                }
            }
            var state = new int[n];   // 0 new, 1 on the path, 2 done
            bool Cycle(int v)
            {
                state[v] = 1;
                foreach (int w in edges[v]) if (state[w] == 1 || (state[w] == 0 && Cycle(w))) return true;
                state[v] = 2;
                return false;
            }
            for (int i = 0; i < n; i++)
                if (state[i] == 0 && Cycle(i)) { issues.Add(Err(pid, $"{where}: lockedUntil makes a cycle (a stop waits on itself).")); break; }
        }

        /// § 12.1 `challenge` / `bonus` (optional; absent = derived from the catalog): WARNINGS only. An unknown kind
        /// is cleared, an unknown / repeated modifier or a 3rd one is dropped. Mutates the stop (what ships).
        internal static void CleanChallenge(PlaylistStop stop, string where, string pid, List<PlaylistIssue> issues)
        {
            // pl-sets: any challenge id (a catalog kind, coins / bad_guys / break, or the attraction's own: hunt, medal …) —
            // lowercase letters, digits, _ or -, ≤ 40; its `<Attraction>.<challenge>` goal block runs (PlaylistChallenge.Authored).
            if (!string.IsNullOrEmpty(stop.challenge) && !PlaylistChallenge.Authored(stop.challenge))
            {
                issues.Add(Warn(pid, $"{where}: stop '{stop.attraction}' challenge '{stop.challenge}' is not a challenge id (lowercase letters, digits, _ or -, up to 40); derived instead."));
                stop.challenge = "";
            }
            if (stop.bonus == null || stop.bonus.Count == 0) return;
            var kept = new List<string>();
            foreach (string b in stop.bonus)
            {
                string why = Array.IndexOf(PlaylistStop.Bonuses, b) < 0 ? $"is not a modifier ({string.Join(", ", PlaylistStop.Bonuses)})"
                    : kept.Contains(b) ? "is listed twice"
                    : kept.Count >= PlaylistStop.MaxBonuses ? $"is past the first {PlaylistStop.MaxBonuses}" : null;
                if (why == null) kept.Add(b);
                else issues.Add(Warn(pid, $"{where}: stop '{stop.attraction}' bonus '{b}' {why}; dropped."));
            }
            stop.bonus = kept;
        }

        /// § 14.3 `continueWith` (the SUGGESTED next playlists of this title): warnings only. A title file names
        /// playlist ids of its own title (`title:<T>:<id>` / `<T>/<id>` of this title are read as `<id>`). The
        /// playlist itself, an unknown id, another title's ref, a repeat, or more than 3 are DROPPED with a warning;
        /// it never fails a playlist. Mutates `p.continueWith` (what the compiler ships).
        internal static void CleanContinueWith(PackagePlaylist p, string titleId, HashSet<string> ids, List<PlaylistIssue> issues)
        {
            if (p?.continueWith == null || p.continueWith.Count == 0) return;
            string pid = string.IsNullOrEmpty(p.id) ? "(no id)" : p.id;
            var kept = new List<string>();
            foreach (string raw in p.continueWith)
            {
                string r = (raw ?? "").Trim();
                if (!string.IsNullOrEmpty(titleId))
                {
                    if (r.StartsWith("title:" + titleId + ":", StringComparison.Ordinal)) r = r.Substring(titleId.Length + 7);
                    else if (r.StartsWith(titleId + "/", StringComparison.Ordinal)) r = r.Substring(titleId.Length + 1);
                }
                string why = r.Length == 0 ? "is empty"
                    : r == p.id ? "is the playlist itself"
                    : !ids.Contains(r) ? "is not a playlist of this title"
                    : kept.Contains(r) ? "is listed twice"
                    : kept.Count >= MaxContinueWith ? $"is past the first {MaxContinueWith}"
                    : null;
                if (why == null) kept.Add(r);
                else issues.Add(Warn(pid, $"continueWith '{raw}' {why}; dropped."));
            }
            p.continueWith = kept;
        }

        public static bool IsValid(string playlistId, List<PlaylistIssue> issues)
            => !issues.Any(i => i.isError && (i.playlistId == null || i.playlistId == playlistId));

        static void ValidatePlaylist(PackagePlaylist p, string pid, string titleId, HashSet<string> kindIds,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve, List<PlaylistIssue> issues,
            List<string> bosses = null)
        {
            // Ownership: a title file ships TITLE playlists of this title only.
            // Cross-title playlists are curated on Web (PLAYLISTS.md § 5).
            if (Array.IndexOf(PackagePlaylist.Owners, p.owner) < 0)
                issues.Add(Err(pid, $"owner '{p.owner}' must be title or platform."));
            else if (p.owner != "title")
                issues.Add(Err(pid, $"owner '{p.owner}' cannot ship in a title package (cross-title playlists are curated on Web)."));
            if (!string.IsNullOrEmpty(p.titleId) && !string.Equals(p.titleId, titleId, StringComparison.Ordinal))
                issues.Add(Err(pid, $"titleId '{p.titleId}' is not this title ({titleId})."));

            PlaylistRules rules = p.rules;
            if (rules.timerSec < 0) issues.Add(Err(pid, "rules.timerSec must be 0 or more."));
            if (rules.lives < 0) issues.Add(Err(pid, "rules.lives must be 0 or more."));
            if (Array.IndexOf(PackagePlaylist.Scorings, rules.scoring) < 0)
                issues.Add(Err(pid, $"rules.scoring '{rules.scoring}' must be sum, best, elimination or placings (or empty)."));
            if (Array.IndexOf(PackagePlaylist.Advances, rules.advance) < 0)
                issues.Add(Err(pid, $"rules.advance '{rules.advance}' must be on_complete or on_timer."));
            else if (rules.advance == "on_timer" && rules.timerSec <= 0)
                issues.Add(Err(pid, "rules.advance on_timer needs rules.timerSec > 0."));

            if (string.IsNullOrWhiteSpace(p.name)) issues.Add(Err(pid, "needs a name."));
            else if (p.name.Length > 60) issues.Add(Err(pid, "name is longer than 60 characters."));
            // roundWord {one, many} (Oct 20): the playlist's word for its rounds — both non-empty, each ≤ 16 characters.
            if (p.roundWord != null && (!string.IsNullOrEmpty(p.roundWord.one) || !string.IsNullOrEmpty(p.roundWord.many)))
            {
                string one = (p.roundWord.one ?? "").Trim(), many = (p.roundWord.many ?? "").Trim();
                if (one.Length == 0 || many.Length == 0) issues.Add(Err(pid, "roundWord needs both one and many (e.g. {\"one\": \"Race\", \"many\": \"Races\"})."));
                if (one.Length > PlaylistRoundWord.MaxChars || many.Length > PlaylistRoundWord.MaxChars)
                    issues.Add(Err(pid, $"roundWord words must be {PlaylistRoundWord.MaxChars} characters or fewer."));
            }
            if (string.IsNullOrWhiteSpace(p.pitch)) issues.Add(Err(pid, "needs a one-sentence pitch."));
            else if (p.pitch.Length > 200) issues.Add(Err(pid, "pitch is longer than 200 characters."));
            if (string.IsNullOrEmpty(p.kind) || !kindIds.Contains(p.kind))
                issues.Add(Err(pid, $"kind '{p.kind}' is not one of the title's kinds ({string.Join(", ", kindIds)})."));
            foreach (string badge in p.badges)
                if (!BadgePattern.IsMatch(badge)) issues.Add(Err(pid, $"badge id '{badge}' is invalid."));
            if (p.badges.Count == 0) issues.Add(Warn(pid, "lists no badges it can earn."));
            if (p.tags.Count == 0) issues.Add(Warn(pid, "has no tags (mood words help Dreamie match requests)."));

            // § 8 record v2: requiresRelease, play {mode, players [min, max]}.
            if (p.requiresRelease < 0) issues.Add(Err(pid, "requiresRelease must be 0 (any) or a release number."));
            PlaylistPlay play = p.play ?? new PlaylistPlay();
            if (!string.IsNullOrEmpty(play.mode) && Array.IndexOf(PlaylistRecordV2.Modes, play.mode) < 0)
                issues.Add(Err(pid, $"play.mode '{play.mode}' must be {string.Join(", ", PlaylistRecordV2.Modes)}."));
            if (play.players != null && play.players.Count > 0
                && (play.players.Count != 2 || play.Min < 1 || play.Max < play.Min || play.Max > PlaylistRecordV2.MaxPlayers))
                issues.Add(Err(pid, $"play.players must be [min, max] with 1 ≤ min ≤ max ≤ {PlaylistRecordV2.MaxPlayers}."));
            if (play.mode == "solo" && play.Max > 1) issues.Add(Warn(pid, "play.mode solo with more than 1 player."));
            if ((play.mode == "versus") && play.Min > 0 && play.Min < 2) issues.Add(Warn(pid, "play.mode versus needs at least 2 players (play.players)."));

            PlaylistFit fit = p.fit;
            if (fit.minRooms < 0) issues.Add(Err(pid, "fit.minRooms must be 0 or more."));
            if (fit.minPlayers < 0 || fit.minPlayers > 16) issues.Add(Err(pid, "fit.minPlayers must be 0–16."));
            if (fit.minAreaM2 < 0f || float.IsNaN(fit.minAreaM2)) issues.Add(Err(pid, "fit.minAreaM2 must be 0 or more."));
            if (!fit.indoor && !fit.outdoor) issues.Add(Err(pid, "fit allows neither indoor nor outdoor."));
            if (fit.minRooms <= 1 && fit.minAreaM2 <= 0f)
                issues.Add(Warn(pid, "has no fit hint (fit.minRooms / fit.minAreaM2)."));

            // pl-sets (PLAYLIST-PLAN § 3): an attraction POOL + Lua, no rounds — the Lua puts the sets out (run.show).
            if (p.arc.Count == 0 && p.pool != null && p.pool.Count > 0) { ValidatePool(p, pid, titleId, resolve, issues); return; }
            if (p.arc.Count == 0) { issues.Add(Err(pid, "has no arc (needs at least one leg) and no pool (a sets playlist: pool + lua).")); return; }
            if (p.arc.Count > MaxLegs) issues.Add(Err(pid, $"has more than {MaxLegs} legs."));

            int climaxes = 0, lastBeat = -1, minutes = 0;
            var legIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < p.arc.Count; i++)
            {
                PlaylistLeg leg = p.arc[i];
                string where = $"leg {i + 1} '{leg.name}'";
                if (string.IsNullOrWhiteSpace(leg.name)) issues.Add(Err(pid, $"leg {i + 1} needs a name."));
                if ((leg.objectiveText ?? "").Length > MaxObjectiveText)
                    issues.Add(Err(pid, $"{where}: objectiveText is longer than {MaxObjectiveText} characters."));
                if (!legIds.Add(leg.id)) issues.Add(Err(pid, $"{where}: leg id '{leg.id}' is used twice."));
                int beat = Array.IndexOf(PackagePlaylist.Beats, leg.beat);
                if (beat < 0) issues.Add(Err(pid, $"{where}: beat '{leg.beat}' must be opening, rising, climax or finale."));
                else
                {
                    if (beat < lastBeat) issues.Add(Err(pid, $"{where}: the arc goes backwards ({leg.beat} after {PackagePlaylist.Beats[lastBeat]})."));
                    lastBeat = Math.Max(lastBeat, beat);
                    if (leg.beat == "climax") climaxes++;
                }
                if (leg.minutes <= 0) issues.Add(Err(pid, $"{where}: estMinutes must be more than 0."));
                else minutes += leg.minutes;
                if (Array.IndexOf(PackagePlaylist.Transitions, leg.next) < 0)
                    issues.Add(Err(pid, $"{where}: next must be walk_on or swap_in_place."));
                if (leg.stops.Count == 0) issues.Add(Err(pid, $"{where}: has no stops."));
                if (leg.stops.Count > MaxStopsPerLeg) issues.Add(Err(pid, $"{where}: has more than {MaxStopsPerLeg} stops."));
                if (leg.mixed) issues.Add(Err(pid, $"{where}: mixed (several titles in one leg) is reserved, not supported yet."));
                // Platform bookend stops (PLAYLISTS.md § 11) are the one exception to one-title-per-leg: they
                // never name the leg's game, and a bookend leg plays this title (no title switch).
                var legTitles = leg.titles.Where(t => !PlaylistBookends.IsPlatformContent(t)).ToList();
                if (legTitles.Count == 0)
                    legTitles = leg.stops.Where(s => !PlaylistBookends.IsPlatformStop(s.contentId, s.attraction))
                        .Select(s => s.contentId ?? titleId).Distinct(StringComparer.Ordinal).ToList();
                if (legTitles.Count == 0) legTitles.Add(titleId);
                if (legTitles.Count > 1) issues.Add(Err(pid, $"{where}: needs {legTitles.Count} titles; v1 plays one title per leg."));
                foreach (string t in legTitles)
                    if (!string.Equals(t, titleId, StringComparison.Ordinal))
                        issues.Add(Err(pid, $"{where}: title '{t}' is not this title (cross-title playlists are curated on Web)."));

                foreach (PlaylistStop stop in leg.stops)
                {
                    CheckOptions(stop, where, pid, issues);
                    if ((stop.objectiveText ?? "").Length > MaxObjectiveText)
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' objectiveText is longer than {MaxObjectiveText} characters."));
                    if ((stop.displayName ?? "").Length > MaxDisplayName)
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' displayName is longer than {MaxDisplayName} characters."));
                    if ((stop.introHint ?? "").Length > MaxObjectiveText)
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' introHint is longer than {MaxObjectiveText} characters."));
                    CleanChallenge(stop, where, pid, issues);
                    if (PlaylistBookends.IsPlatformStop(stop.contentId, stop.attraction))
                    {
                        CheckPlatformStop(stop, where, pid, issues);
                        continue;
                    }
                    if (stop.contentId != null && !legTitles.Contains(stop.contentId))
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' is from '{stop.contentId}', which the leg's titles do not list."));
                    if (Array.IndexOf(PackagePlaylist.Roles, stop.role) < 0)
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' role '{stop.role}' must be start, stage, miniboss, boss or end."));
                    if (Array.IndexOf(PackagePlaylist.Placements, stop.placement.prefer) < 0)
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' placement.prefer '{stop.placement.prefer}' must be entrance, biggest, outdoor, far, any or players."));
                    CheckAttraction(stop.attraction, $"{where}: stop", pid, resolve, issues);
                    foreach (string alternate in stop.alternates)
                        CheckAttraction(alternate, $"{where}: alternate of '{stop.attraction}'", pid, resolve, issues);
                }

                CheckLocks(leg, where, pid, issues);
                foreach (PlaylistStop stop in leg.stops)
                {
                    if ((stop.lockSign ?? "").Length > MaxObjectiveText)
                        issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' lockSign is longer than {MaxObjectiveText} characters."));
                    if (!string.IsNullOrEmpty(stop.lockSign) && (stop.lockedUntil == null || stop.lockedUntil.Count == 0))
                        issues.Add(Warn(pid, $"{where}: stop '{stop.attraction}' has a lockSign but no lockedUntil; it never shows."));
                    // § 8.6 locks are data: playlist Lua must not duplicate a lockedUntil lock (the runtime enforces it natively).
                    if (stop.lockedUntil != null && stop.lockedUntil.Count > 0 && !string.IsNullOrEmpty(p.lua) && !string.IsNullOrEmpty(stop.id)
                        && System.Text.RegularExpressions.Regex.IsMatch(p.lua, @"\[\s*['""]" + System.Text.RegularExpressions.Regex.Escape(stop.id) + @"['""]\s*\]\s*=\s*\{\s*needs|run\.lock\(\s*['""]" + System.Text.RegularExpressions.Regex.Escape(stop.id) + @"['""]"))
                        issues.Add(Warn(pid, $"{where}: the playlist Lua locks '{stop.id}', which its lockedUntil already locks natively; keep Lua for dynamic logic only."));
                }
                CheckBonusLevels(leg, where, pid, resolve, bosses, issues);
                if (p.noBonus && leg.bonusLevels != null && leg.bonusLevels.Count > 0)
                    issues.Add(Warn(pid, $"{where}: the playlist sets noBonus, so its bonusLevels are never used."));
                if (p.wholeSpaceArena && p.noBonus)
                    issues.Add(Warn(pid, $"{where}: wholeSpaceArena fills the space from bonusLevels, but noBonus leaves it empty."));
                else if (p.wholeSpaceArena && (leg.bonusLevels == null || leg.bonusLevels.Count == 0))
                    issues.Add(Warn(pid, $"{where}: wholeSpaceArena with no bonusLevels: only the leg's own stops make the arena."));

                PlaylistObjective o = leg.objective;
                bool hasBoss = leg.stops.Any(s => s.role == "boss" || s.role == "miniboss");
                if (string.IsNullOrEmpty(o.type) || Array.IndexOf(PackagePlaylist.Objectives, o.type) < 0)
                    issues.Add(Err(pid, $"{where}: objective.type '{o.type}' must be reach_exit, defeat_boss, score, collect, survive or time."));
                else
                {
                    if ((o.type == "score" || o.type == "collect") && o.count <= 0 && !o.derived)
                        issues.Add(Err(pid, $"{where}: a {o.type} objective needs count > 0."));
                    if ((o.type == "survive" || (o.type == "time" && rules.advance != "on_timer")) && o.seconds <= 0 && !o.derived)
                        issues.Add(Err(pid, $"{where}: a {o.type} objective needs seconds > 0."));
                    if (o.type == "defeat_boss" && !hasBoss)
                        issues.Add(Err(pid, $"{where}: defeat_boss needs a boss or miniboss stop in this leg."));
                }
                if (!string.IsNullOrEmpty(o.target) && !leg.stops.Any(s => SameAttraction(s.attraction, o.target, resolve)))
                    issues.Add(Err(pid, $"{where}: objective.target '{o.target}' is not a stop of this leg."));
                if (leg.beat == "climax" && !hasBoss && o.type != "score" && o.type != "survive")
                    issues.Add(Err(pid, $"{where}: the climax needs a boss/miniboss stop, or a score/survive showdown."));
                if (leg.beat == "climax" && leg.stops.Count > 0
                    && leg.stops.All(s => PlaylistBookends.IsPlatformStop(s.contentId, s.attraction)))
                    issues.Add(Err(pid, $"{where}: the climax must be one of the title's own legs, not a bookend."));
            }

            if (climaxes == 0) issues.Add(Err(pid, "the arc has no climax leg."));
            if (climaxes > 1) issues.Add(Err(pid, "the arc has more than one climax leg."));
            PlaylistLeg first = p.arc[0], last = p.arc[p.arc.Count - 1];
            if (last.beat != "climax" && last.beat != "finale")
                issues.Add(Err(pid, "the arc must end with its climax or a finale."));
            if (p.arc.Count > 1 && first.beat != "opening")
                issues.Add(Err(pid, "the first leg must be the opening."));
            if (last.objective.type == "time" && !last.objective.derived && p.rules.advance != "on_timer"
                && !(last.beat == "finale" && climaxes == 1))
                issues.Add(Err(pid, "the ending needs a real end condition (time is only for a finale after the climax)."));
            if (first.stops.Count > 0 && first.stops[0].role != "start")
                issues.Add(Warn(pid, "the first stop is not role 'start'."));
            if (minutes > 60) issues.Add(Warn(pid, $"runs {minutes} minutes (over an hour)."));
        }

        public const int MaxOptionsBytes = 2048, MaxObjectiveText = 120, MaxDisplayName = 60;
        /// pl-sets: a pool holds at most this many attractions.
        public const int MaxPool = 64;
        static readonly string[] PoolPlacements = { "any", "biggest", "entrance", "outdoor" };

        /// A SETS record (pl-sets, PLAYLIST-PLAN § 3.1): `pool` = the attractions the Lua may put out, each with the challenge it
        /// offers (`coins`, `bad_guys`, `break` or the attraction's own id) and its options; `lua` puts the sets out
        /// (run.show / run.add / run.remove, or the run.batches library). No `arc`.
        internal static void ValidatePool(PackagePlaylist p, string pid, string titleId,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve, List<PlaylistIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(p.lua)) issues.Add(Err(pid, "a pool playlist needs lua that puts its sets out (run.show / run.batches)."));
            else if (!PlaylistSets.DrivesSets(p.lua)) issues.Add(Err(pid, "its lua never puts a set out: call run.show{…} (or return run.batches{…})."));
            if (p.pool.Count > MaxPool) issues.Add(Err(pid, $"its pool has more than {MaxPool} attractions."));
            var refs = new HashSet<string>(StringComparer.Ordinal);
            int bosses = 0;
            for (int i = 0; i < p.pool.Count; i++)
            {
                PlaylistStop stop = p.pool[i];
                string where = $"pool {i + 1} '{(string.IsNullOrEmpty(stop.id) ? stop.attraction : stop.id)}'";
                if (string.IsNullOrWhiteSpace(stop.attraction)) { issues.Add(Err(pid, $"{where}: needs an attraction.")); continue; }
                if (!string.IsNullOrEmpty(stop.id) && !IdPattern.IsMatch(stop.id)) issues.Add(Err(pid, $"{where}: id '{stop.id}' must be lowercase letters, digits, - or _."));
                string reference = string.IsNullOrEmpty(stop.id) ? stop.attraction : stop.id;
                if (!refs.Add(reference)) issues.Add(Err(pid, $"{where}: '{reference}' is in the pool twice (give each its own id)."));
                if (PlaylistBookends.IsPlatformStop(stop.contentId, stop.attraction)) { issues.Add(Err(pid, $"{where}: platform bookends are not pool attractions.")); continue; }
                if (!string.IsNullOrEmpty(stop.contentId) && !string.Equals(stop.contentId, titleId, StringComparison.Ordinal))
                    issues.Add(Err(pid, $"{where}: from '{stop.contentId}', not this title (cross-title playlists are curated on Web)."));
                if (Array.IndexOf(PackagePlaylist.Roles, stop.role) < 0)
                    issues.Add(Err(pid, $"{where}: role '{stop.role}' must be start, stage, miniboss, boss or end."));
                if (stop.role == "boss") bosses++;
                if (!string.IsNullOrEmpty(stop.challenge) && !PlaylistChallenge.Authored(stop.challenge))
                    issues.Add(Err(pid, $"{where}: challenge '{stop.challenge}' must be coins, bad_guys, break or the attraction's own id (lowercase)."));
                if (string.IsNullOrEmpty(stop.challenge)) issues.Add(Warn(pid, $"{where}: names no challenge (coins, bad_guys, break or its own)."));
                if (Array.IndexOf(PoolPlacements, stop.placement?.prefer ?? "any") < 0)
                    issues.Add(Err(pid, $"{where}: placement '{stop.placement?.prefer}' must be biggest, entrance, outdoor or any (far_from is the Lua's run.show place)."));
                CheckOptions(stop, where, pid, issues);
                if ((stop.displayName ?? "").Length > MaxDisplayName) issues.Add(Err(pid, $"{where}: displayName is longer than {MaxDisplayName} characters."));
                foreach (var (text, field) in new[] { (stop.objectiveText, "objectiveText"), (stop.introHint, "introHint"), (stop.lockSign, "lockSign") })
                    if ((text ?? "").Length > MaxObjectiveText) issues.Add(Err(pid, $"{where}: {field} is longer than {MaxObjectiveText} characters."));
                if (stop.lockedUntil != null && stop.lockedUntil.Count > 0)
                    issues.Add(Warn(pid, $"{where}: lockedUntil is for arc rounds; a sets playlist locks with run.show{{lock}} / run.lock."));
                CheckAttraction(stop.attraction, $"{where}:", pid, resolve, issues);
                foreach (string alternate in stop.alternates ?? new List<string>())
                    CheckAttraction(alternate, $"{where}: alternate", pid, resolve, issues);
                if (!string.IsNullOrEmpty(p.lua) && p.lua.IndexOf(reference, StringComparison.Ordinal) < 0)
                    issues.Add(Warn(pid, $"{where}: its lua never names '{reference}' (never put out?)."));
            }
            if (bosses > 1) issues.Add(Warn(pid, $"its pool has {bosses} bosses."));
        }

        /// § 2 stop `options`: a JSON object, at most 2 KB. Opaque otherwise.
        static void CheckOptions(PlaylistStop stop, string where, string pid, List<PlaylistIssue> issues)
        {
            if (string.IsNullOrEmpty(stop.options)) return;
            if (!stop.options.StartsWith("{", StringComparison.Ordinal))
                issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' options must be a JSON object."));
            else if (System.Text.Encoding.UTF8.GetByteCount(stop.options) > MaxOptionsBytes)
                issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' options are larger than {MaxOptionsBytes} bytes."));
        }

        /// § 6.0 AUTHORED BONUS: every `bonusLevels` entry is one of the title's own attractions (never a bookend),
        /// not a boss unless the leg says `bonusBosses`; one already a stop of this round is only warned about (the
        /// planner never doubles a round's own stop).
        internal static void CheckBonusLevels(PlaylistLeg leg, string where, string pid,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve, List<string> bosses, List<PlaylistIssue> issues)
        {
            if (leg.bonusLevels == null) return;
            if (leg.noBonus && leg.bonusLevels.Count > 0)
                issues.Add(Warn(pid, $"{where}: noBonus is set, so its bonusLevels are never used."));
            foreach (string b in leg.bonusLevels)
            {
                if (PlaylistBookends.IdOf(b) != null) { issues.Add(Err(pid, $"{where}: bonusLevels '{b}' is a platform bookend, not a level.")); continue; }
                CheckAttraction(b, $"{where}: bonusLevels", pid, resolve, issues);
                // A boss: a boss / miniboss stop of any of the title's playlists, or one named so (A_LT_Boss_Mainframe).
                bool namedBoss = System.Text.RegularExpressions.Regex.IsMatch(b, @"(^|[_/a-z])Boss([A-Z_]|$)");
                if (!leg.bonusBosses && (namedBoss || (bosses != null && bosses.Any(x => SameAttraction(x, b, resolve)))))
                    issues.Add(Err(pid, $"{where}: bonusLevels '{b}' is a boss; set bonusBosses: true to allow it as bonus."));
                if (leg.stops.Any(s => SameAttraction(s.attraction, b, resolve)))
                    issues.Add(Warn(pid, $"{where}: bonusLevels '{b}' is already a stop of this round; it is never doubled."));
            }
        }

        static void CheckAttraction(string reference, string where, string pid,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve, List<PlaylistIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(reference)) { issues.Add(Err(pid, where + " names no attraction.")); return; }
            IReadOnlyList<PlaylistAttractionRef> found = resolve(reference);
            if (found.Count == 0) issues.Add(Err(pid, $"{where} '{reference}' is not an attraction of this title."));
            else if (found.Count > 1)
                issues.Add(Err(pid, $"{where} '{reference}' matches {found.Count} prefabs; use its full resourceName."));
            else if (!found[0].hasPackingBake)
                issues.Add(Err(pid, $"{where} '{reference}' has no packing bake (open it and bake its footprint)."));
        }

        /// A platform bookend stop (PLAYLISTS.md § 11): a known platform id, no alternates.
        static void CheckPlatformStop(PlaylistStop stop, string where, string pid, List<PlaylistIssue> issues)
        {
            if (!PlaylistBookends.IsKnown(stop.attraction))
                issues.Add(Err(pid, $"{where}: '{stop.attraction}' is not a platform bookend ({string.Join(", ", PlaylistBookends.Ids)})."));
            if (stop.alternates.Count > 0)
                issues.Add(Err(pid, $"{where}: bookend '{stop.attraction}' takes no alternates."));
            if (Array.IndexOf(PackagePlaylist.Roles, stop.role) < 0)
                issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' role '{stop.role}' must be start, stage, miniboss, boss or end."));
            if (Array.IndexOf(PackagePlaylist.Placements, stop.placement.prefer) < 0)
                issues.Add(Err(pid, $"{where}: stop '{stop.attraction}' placement.prefer '{stop.placement.prefer}' must be entrance, biggest, outdoor, far, any or players."));
        }

        static bool SameAttraction(string a, string b, Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            string pa = PlaylistBookends.IdOf(a), pb = PlaylistBookends.IdOf(b);
            if (pa != null || pb != null) return pa == pb;
            IReadOnlyList<PlaylistAttractionRef> ra = resolve(a), rb = resolve(b);
            return ra.Count == 1 && rb.Count == 1 && ra[0].guid == rb[0].guid;
        }

        static PlaylistIssue Err(string id, string message) => new PlaylistIssue { playlistId = id, isError = true, message = message };
        static PlaylistIssue Warn(string id, string message) => new PlaylistIssue { playlistId = id, isError = false, message = message };
    }
}
#endif
