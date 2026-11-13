#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Defective.JSON;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// The authoring JSON of a title's playlists (Docs/PLAYLISTS.md § 2), read and written WITHOUT
    /// JsonUtility so the Studio, the generator post-processing and the offline tests share one pure
    /// path. Writes authored fields (never the compiled ones), in a stable order with 2-space
    /// indents, and stop `options` as JSON objects — so a Studio save of a hand-written file diffs cleanly.
    ///
    /// SAVE KEEPS EVERY FIELD (repair Oct 20 2026). Each object read from a file (the root, every playlist,
    /// leg and stop) remembers its raw JSON and what the model made of it. On write, a field the model did not
    /// change is written back exactly as it was read; a field the model changed is written from the model; a
    /// field the model does not know (campaigns, flags, layout, story, scene, choice, goal, lua, dreamie,
    /// timing, …) passes through untouched, in its original place. Unknown fields are reached with
    /// <see cref="Extra"/> / <see cref="SetExtra"/> (the Studio's goal-block, playlist-Lua and Dreamie editors).
    /// </summary>
    public static class PlaylistStudioJson
    {
        // ── read ──

        /// <summary>Throws InvalidOperationException on a broken or newer file (same messages as ContentPlaylistStore).</summary>
        public static ContentPlaylistStore.Data ReadData(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("Playlists file is empty.");
            JSONObject root;
            try { root = new JSONObject(json); }
            catch (Exception e) { throw new InvalidOperationException("Playlists file is not valid JSON: " + e.Message); }
            if (root == null || root.type != JSONObject.Type.Object)
                throw new InvalidOperationException("Playlists file is not a JSON object.");
            int version = Int(root, "schemaVersion");
            if (version < 1) throw new InvalidOperationException("Playlists file has no schemaVersion (expected 1).");
            if (version > ContentPlaylistStore.CurrentVersion)
                throw new InvalidOperationException(
                    $"Playlists file is schema v{version}; this SDK reads up to v{ContentPlaylistStore.CurrentVersion}.");
            var data = new ContentPlaylistStore.Data
            {
                schemaVersion = version,
                kinds = ReadKinds(root.GetField("kinds")),
                playlists = new List<PackagePlaylist>(),
            };
            foreach (JSONObject p in Items(root.GetField("playlists")))
            {
                PackagePlaylist playlist = ReadPlaylist(p);
                if (playlist != null) data.playlists.Add(playlist);
            }
            // § 15.1 title-level campaigns / flags (JSON text, as ContentPlaylistStore carries them) and § 6.0a layout.
            JSONObject campaigns = root.GetField("campaigns"), flags = root.GetField("flags");
            data.campaignsJson = campaigns != null && campaigns.type == JSONObject.Type.Array ? campaigns.Print() : "";
            data.flagsJson = flags != null && flags.type == JSONObject.Type.Array ? flags.Print() : "";
            data.layout = Str(root, "layout") ?? "";
            Remember(data, root, CanonRoot(data));
            if (IsExpandedStyle(json)) ExpandedStyle.Add(data, true);
            return data;
        }

        public static List<PlaylistKind> ReadKinds(JSONObject array)
        {
            var kinds = new List<PlaylistKind>();
            foreach (JSONObject k in Items(array))
                if (k.type == JSONObject.Type.Object)
                    kinds.Add(new PlaylistKind { id = Str(k, "id"), singular = Str(k, "singular"), plural = Str(k, "plural") });
            return kinds;
        }

        /// `roundWord` {one, many} (or a bare "Race") → the model; null when absent / empty.
        static PlaylistRoundWord RoundWord(JSONObject j)
        {
            if (j == null) return null;
            if (j.type == JSONObject.Type.String) return string.IsNullOrWhiteSpace(j.stringValue) ? null : new PlaylistRoundWord { one = j.stringValue.Trim() };
            if (j.type != JSONObject.Type.Object) return null;
            string one = Str(j, "one"), many = Str(j, "many");
            return string.IsNullOrWhiteSpace(one) ? null : new PlaylistRoundWord { one = one.Trim(), many = string.IsNullOrWhiteSpace(many) ? null : many.Trim() };
        }

        /// <summary>One authoring playlist object → a normalized PackagePlaylist (null when not an object).</summary>
        public static PackagePlaylist ReadPlaylist(JSONObject o)
        {
            if (o == null || o.type != JSONObject.Type.Object) return null;
            // Record v2 (CONTRACT § 8): a v1 playlist is upgraded on read; the Studio remembers the UPGRADED object, so a
            // save writes v2 (schema, estMinutes, colocated Dreamie guidance, a derivable objective dropped) and nothing else.
            if (!PlaylistRecordV2.IsV2(o)) { o = DeepCopy(o); PlaylistRecordV2.Upgrade(o); }
            var p = new PackagePlaylist
            {
                id = Str(o, "id"),
                owner = Str(o, "owner"),
                titleId = Str(o, "titleId"),
                kind = Str(o, "kind"),
                name = Str(o, "name"),
                pitch = Str(o, "pitch"),
                art = Str(o, "art"),
                tags = Strings(o.GetField("tags")),
                badges = Strings(o.GetField("badges")),
                continueWith = Strings(o.GetField(PlaylistGates.ContinueWithField)),
                chapter = Str(o, PlaylistGates.ChapterField),
                noBonus = Bool(o, "noBonus", false),
                wholeSpaceArena = Bool(o, "wholeSpaceArena", false),
                roundWord = RoundWord(o.GetField("roundWord")),
                schema = Str(o, "schema"),
                requiresRelease = Int(o, "requiresRelease"),
                play = ReadPlay(o.GetField("play")),
            };
            // § 15.1 gates, the same carrier as ContentPlaylistStore.AuthoringAsText: canonical JSON text + projection.
            JSONObject req = o.GetField(PlaylistGates.RequiresField), sets = o.GetField(PlaylistGates.SetsField);
            if (req != null && req.type != JSONObject.Type.Null)
                PlaylistGates.SetRequires(p, PlaylistGates.ReadRequires(req).Select(PlaylistGates.Parse));
            if (sets != null && sets.type != JSONObject.Type.Null)
            {
                var flagsAndBadges = PlaylistGates.ReadSets(sets);
                PlaylistGates.SetSets(p, flagsAndBadges.Where(x => x.StartsWith("flag:")).Select(x => x.Substring(5)),
                    flagsAndBadges.Where(x => x.StartsWith("badge:")).Select(x => x.Substring(6)));
            }
            JSONObject window = o.GetField("window"), personal = o.GetField("personal");
            if (window != null && window.type == JSONObject.Type.Object) p.windowJson = window.Print();
            if (personal != null && personal.type == JSONObject.Type.Array) p.personalJson = personal.Print();
            JSONObject fit = o.GetField("fit");
            if (fit != null && fit.type == JSONObject.Type.Object)
                p.fit = new PlaylistFit
                {
                    minRooms = Int(fit, "minRooms"),
                    minAreaM2 = Float(fit, "minAreaM2"),
                    indoor = Bool(fit, "indoor", true),
                    outdoor = Bool(fit, "outdoor", true),
                    minPlayers = Int(fit, "minPlayers"),
                };
            JSONObject rules = o.GetField("rules");
            if (rules != null && rules.type == JSONObject.Type.Object)
                p.rules = new PlaylistRules
                {
                    timerSec = Int(rules, "timerSec"),
                    scoring = Str(rules, "scoring") ?? "",
                    lives = Int(rules, "lives"),
                    advance = Str(rules, "advance") ?? "on_complete",
                    script = Str(rules, "script") ?? "",
                };
            p.arc = new List<PlaylistLeg>();
            var pending = new List<(object model, JSONObject raw)>();
            foreach (JSONObject l in Items(o.GetField("arc")))
            {
                if (l.type != JSONObject.Type.Object) continue;
                var leg = new PlaylistLeg
                {
                    id = Str(l, "id"),
                    name = Str(l, "name"),
                    beat = Str(l, "beat"),
                    minutes = l.GetField("estMinutes") != null ? Int(l, "estMinutes") : Int(l, "minutes"),
                    next = Str(l, "next"),
                    titles = Strings(l.GetField("titles")),
                    mixed = Bool(l, "mixed", false),
                    objectiveText = Str(l, "objectiveText"),
                    bonusLevels = Strings(l.GetField("bonusLevels")),
                    bonusBosses = Bool(l, "bonusBosses", false),
                    noBonus = Bool(l, "noBonus", false),
                    stops = new List<PlaylistStop>(),
                };
                JSONObject obj = l.GetField("objective");
                if (obj != null && obj.type == JSONObject.Type.Object)
                    leg.objective = new PlaylistObjective
                    {
                        type = Str(obj, "type"), target = Str(obj, "target"),
                        count = Int(obj, "count"), seconds = Int(obj, "seconds"),
                    };
                foreach (JSONObject s in Items(l.GetField("stops")))
                {
                    if (s.type != JSONObject.Type.Object) continue;
                    var stop = new PlaylistStop
                    {
                        contentId = Str(s, "contentId"),
                        attraction = Str(s, "attraction"),
                        role = Str(s, "role"),
                        alternates = Strings(s.GetField("alternates")),
                        objectiveText = Str(s, "objectiveText"),
                        displayName = Str(s, "displayName"),
                        challenge = Str(s, "challenge"),
                        bonus = Strings(s.GetField("bonus")),
                        introHint = Str(s, "introHint"),
                        id = Str(s, "id"),
                        lockedUntil = Strings(s.GetField("lockedUntil")),
                        lockSign = Str(s, "lockSign"),
                    };
                    JSONObject placement = s.GetField("placement");
                    stop.placement = new PlaylistPlacement
                    {
                        prefer = (placement != null && placement.type == JSONObject.Type.Object ? Str(placement, "prefer") : null)
                            ?? Str(s, "prefer") ?? "any",
                    };
                    JSONObject options = s.GetField("options");
                    if (options != null && options.type != JSONObject.Type.Null)
                        stop.options = options.type == JSONObject.Type.String ? options.stringValue : options.Print();
                    leg.stops.Add(stop);
                    pending.Add((stop, s));
                }
                p.arc.Add(leg);
                pending.Add((leg, l));
            }
            var underived = new HashSet<PlaylistLeg>(p.arc.Where(l => l.objective == null || string.IsNullOrEmpty(l.objective.type)));
            ContentPlaylistStore.Normalize(p);
            // A v2 round's objective is DERIVED by Normalize: remember it, so a save never writes it back as authored.
            foreach (PlaylistLeg l in underived) MarkDerived(l.objective);
            // What the model made of each object, AFTER normalizing: an unedited field compares equal on write.
            foreach (var (model, raw) in pending)
                Remember(model, raw, model is PlaylistStop st ? CanonStop(st) : CanonLeg((PlaylistLeg)model));
            Remember(p, o, CanonPlaylist(p, p.titleId));
            return p;
        }

        static PlaylistPlay ReadPlay(JSONObject j)
        {
            var play = new PlaylistPlay();
            if (j == null || j.type != JSONObject.Type.Object) return play;
            play.mode = Str(j, "mode") ?? "";
            foreach (JSONObject x in Items(j.GetField("players")))
                if (x.type == JSONObject.Type.Number && play.players.Count < 2) play.players.Add((int)Math.Round(x.doubleValue));
            return play;
        }

        // A round objective Normalize derived (v2 has none): (its values then) — written only if the author changed it.
        static readonly ConditionalWeakTable<PlaylistObjective, string> Derived = new ConditionalWeakTable<PlaylistObjective, string>();
        static string ObjKey(PlaylistObjective o) => o == null ? "" : $"{o.type}|{o.target}|{o.count}|{o.seconds}";
        static void MarkDerived(PlaylistObjective o) { if (o == null) return; Derived.Remove(o); Derived.Add(o, ObjKey(o)); }

        /// Record v2: does the round need its objective written? Only a v1 one the derivation cannot reproduce (a timed /
        /// scored round), or one the author set by hand that is not derivable.
        public static bool WritesObjective(PlaylistLeg leg)
        {
            PlaylistObjective o = leg?.objective;
            if (o == null || string.IsNullOrEmpty(o.type)) return false;
            if (Derived.TryGetValue(o, out string was) && was == ObjKey(o)) return false;   // derived and untouched
            var lj = JSONObject.emptyObject;
            var stops = JSONObject.emptyArray;
            foreach (PlaylistStop s in leg.stops ?? new List<PlaylistStop>())
            {
                if (s == null) continue;
                var sj = JSONObject.emptyObject; sj.AddField("attraction", s.attraction ?? ""); sj.AddField("role", s.role ?? "");
                stops.Add(sj);
            }
            lj.AddField("stops", stops);
            var oj = JSONObject.emptyObject;
            oj.AddField("type", o.type);
            if (!string.IsNullOrEmpty(o.target)) oj.AddField("target", o.target);
            if (o.count > 0) oj.AddField("count", o.count);
            if (o.seconds > 0) oj.AddField("seconds", o.seconds);
            return !PlaylistRecordV2.ObjectiveDerivable(lj, oj);
        }

        // ── write ──

        /// A file read with every scalar array expanded one item per line (Python `json.dumps(indent=2)` style) is
        /// written back the same way, so a Studio save of an agent-written file diffs only where something changed.
        public static string Serialize(ContentPlaylistStore.Data data, string titleId)
            => Pretty(WriteData(data, titleId), data != null && ExpandedStyle.TryGetValue(data, out _)) + "\n";

        static readonly ConditionalWeakTable<object, object> ExpandedStyle = new ConditionalWeakTable<object, object>();

        /// True when the text has multi-line scalar arrays and no one-line non-empty array.
        static bool IsExpandedStyle(string json)
            => System.Text.RegularExpressions.Regex.IsMatch(json, @"\[\s*\n\s*(""|-?\d|true|false|null)")
               && !System.Text.RegularExpressions.Regex.IsMatch(json, @"\[[ \t]*(""|-?\d|true|false|null)[^\n]*\]");

        public static JSONObject WriteData(ContentPlaylistStore.Data data, string titleId)
        {
            JSONObject root = CanonRoot(data);
            var list = JSONObject.emptyArray;
            foreach (PackagePlaylist p in data?.playlists ?? new List<PackagePlaylist>())
                if (p != null) list.Add(WritePlaylist(p, titleId));
            root.AddField("playlists", list);
            return data == null ? root : Merge(OriginOf(data), root, "playlists");
        }

        /// <summary>Authored fields only, defaults left out where the contract says absent = default; every field the
        /// model did not change (and every field it does not know) comes back exactly as it was read.</summary>
        public static JSONObject WritePlaylist(PackagePlaylist p, string titleId)
        {
            // A playlist read from a file keeps its own titleId (absent stays absent); a new one is stamped with the title.
            JSONObject o = CanonPlaylist(p, OriginOf(p) != null ? p.titleId : titleId);
            var arc = JSONObject.emptyArray;
            foreach (PlaylistLeg leg in p.arc ?? new List<PlaylistLeg>())
                if (leg != null) arc.Add(WriteLeg(leg));
            o.AddField("arc", arc);
            return Merge(OriginOf(p), o, "arc");
        }

        static JSONObject WriteLeg(PlaylistLeg leg)
        {
            JSONObject l = CanonLeg(leg);
            var stops = JSONObject.emptyArray;
            foreach (PlaylistStop stop in leg.stops ?? new List<PlaylistStop>())
                if (stop != null) stops.Add(Merge(OriginOf(stop), CanonStop(stop)));
            l.AddField("stops", stops);
            return Merge(OriginOf(leg), l, "stops");
        }

        /// The root's own fields (no `playlists`).
        static JSONObject CanonRoot(ContentPlaylistStore.Data data)
        {
            var root = JSONObject.emptyObject;
            root.AddField("schemaVersion", Math.Max(1, data?.schemaVersion ?? 1));
            var kinds = JSONObject.emptyArray;
            foreach (PlaylistKind k in data?.kinds ?? new List<PlaylistKind>())
            {
                if (k == null) continue;
                var ko = JSONObject.emptyObject;
                ko.AddField("id", k.id ?? "");
                ko.AddField("singular", k.singular ?? "");
                ko.AddField("plural", k.plural ?? "");
                kinds.Add(ko);
            }
            if (kinds.count > 0) root.AddField("kinds", kinds);
            JSONObject campaigns = PlaylistGates.SafeParse(data?.campaignsJson), flags = PlaylistGates.SafeParse(data?.flagsJson);
            if (campaigns != null && campaigns.type == JSONObject.Type.Array && campaigns.count > 0) root.AddField("campaigns", campaigns);
            if (flags != null && flags.type == JSONObject.Type.Array && flags.count > 0) root.AddField("flags", flags);
            if (!string.IsNullOrEmpty(data?.layout)) root.AddField("layout", data.layout);
            return root;
        }

        /// A playlist's own fields (no `arc`).
        static JSONObject CanonPlaylist(PackagePlaylist p, string titleId)
        {
            var o = JSONObject.emptyObject;
            o.AddField("id", p.id ?? "");
            o.AddField("schema", PlaylistRecordV2.Schema);   // every writer emits v2 (CONTRACT § 8)
            o.AddField("owner", string.IsNullOrEmpty(p.owner) ? "title" : p.owner);
            if (!string.IsNullOrEmpty(p.titleId)) o.AddField("titleId", p.titleId);
            else if (!string.IsNullOrEmpty(titleId)) o.AddField("titleId", titleId);
            o.AddField("kind", p.kind ?? "");
            o.AddField("name", p.name ?? "");
            o.AddField("pitch", p.pitch ?? "");
            if (p.roundWord != null && !string.IsNullOrWhiteSpace(p.roundWord.one))
            {
                var rw = JSONObject.emptyObject; var (one, many) = PlaylistRoundWord.Of(p.roundWord);
                rw.AddField("one", one); rw.AddField("many", many);
                o.AddField("roundWord", rw);
            }
            if (!string.IsNullOrEmpty(p.art)) o.AddField("art", p.art);
            o.AddField("tags", StringArray(p.tags));
            o.AddField("badges", StringArray(p.badges));
            if (!string.IsNullOrEmpty(p.chapter)) o.AddField(PlaylistGates.ChapterField, p.chapter);
            var requires = PlaylistGates.Requires(p);
            if (requires.Count > 0) o.AddField(PlaylistGates.RequiresField, PlaylistGates.WriteRequires(requires.Select(PlaylistGates.Encode)));
            var sets = PlaylistGates.Sets(p);
            if (sets.flags.Count > 0 || sets.badges.Count > 0)
                o.AddField(PlaylistGates.SetsField, PlaylistGates.WriteSets(sets.flags.Select(f => "flag:" + f.flag + (f.value == null ? "" : "=" + f.value)).Concat(sets.badges.Select(b => "badge:" + b))));
            JSONObject window = PlaylistGates.SafeParse(p.windowJson), personal = PlaylistGates.SafeParse(p.personalJson);
            if (window != null && window.type == JSONObject.Type.Object && window.count > 0) o.AddField("window", window);
            if (personal != null && personal.type == JSONObject.Type.Array && personal.count > 0) o.AddField("personal", personal);
            if (p.continueWith != null && p.continueWith.Count > 0) o.AddField(PlaylistGates.ContinueWithField, StringArray(p.continueWith));
            PlaylistFit f = p.fit ?? new PlaylistFit();
            var fit = JSONObject.emptyObject;
            fit.AddField("minRooms", f.minRooms);
            AddNumber(fit, "minAreaM2", f.minAreaM2);
            fit.AddField("indoor", f.indoor);
            fit.AddField("outdoor", f.outdoor);
            o.AddField("fit", fit);
            // v2: play {mode, players [min, max]} replaces fit.minPlayers; requiresRelease = the title release it needs.
            PlaylistPlay pl = p.play ?? new PlaylistPlay();
            if (!string.IsNullOrEmpty(pl.mode) || (pl.players != null && pl.players.Count > 0))
            {
                var play = JSONObject.emptyObject;
                if (!string.IsNullOrEmpty(pl.mode)) play.AddField("mode", pl.mode);
                if (pl.players != null && pl.players.Count > 0) { var pa = JSONObject.emptyArray; foreach (int n in pl.players) pa.Add(n); play.AddField("players", pa); }
                o.AddField("play", play);
            }
            if (p.requiresRelease > 0) o.AddField("requiresRelease", p.requiresRelease);
            if (p.rules != null && !p.rules.IsDefault)
            {
                var rules = JSONObject.emptyObject;
                rules.AddField("timerSec", p.rules.timerSec);
                rules.AddField("scoring", p.rules.scoring ?? "");
                rules.AddField("lives", p.rules.lives);
                rules.AddField("advance", string.IsNullOrEmpty(p.rules.advance) ? "on_complete" : p.rules.advance);
                if (!string.IsNullOrEmpty(p.rules.script)) rules.AddField("script", p.rules.script);
                o.AddField("rules", rules);
            }
            if (p.noBonus) o.AddField("noBonus", true);
            if (p.wholeSpaceArena) o.AddField("wholeSpaceArena", true);
            return o;
        }

        /// A leg's own fields (no `stops`).
        static JSONObject CanonLeg(PlaylistLeg leg)
        {
            var l = JSONObject.emptyObject;
            if (!string.IsNullOrEmpty(leg.id)) l.AddField("id", leg.id);
            l.AddField("name", leg.name ?? "");
            l.AddField("beat", leg.beat ?? "");
            l.AddField("estMinutes", leg.minutes);
            l.AddField("next", string.IsNullOrEmpty(leg.next) ? "walk_on" : leg.next);
            if (leg.titles != null && leg.titles.Count > 0) l.AddField("titles", StringArray(leg.titles));
            if (leg.mixed) l.AddField("mixed", true);
            if (WritesObjective(leg))
            {
                var obj = JSONObject.emptyObject;
                PlaylistObjective ob = leg.objective;
                obj.AddField("type", ob.type ?? "");
                if (!string.IsNullOrEmpty(ob.target)) obj.AddField("target", ob.target);
                if (ob.count > 0) obj.AddField("count", ob.count);
                if (ob.seconds > 0) obj.AddField("seconds", ob.seconds);
                l.AddField("objective", obj);
            }
            if (!string.IsNullOrEmpty(leg.objectiveText)) l.AddField("objectiveText", leg.objectiveText);
            if (leg.bonusLevels != null && leg.bonusLevels.Count > 0) l.AddField("bonusLevels", StringArray(leg.bonusLevels));
            if (leg.bonusBosses) l.AddField("bonusBosses", true);
            if (leg.noBonus) l.AddField("noBonus", true);
            return l;
        }

        static JSONObject CanonStop(PlaylistStop stop)
        {
            var s = JSONObject.emptyObject;
            if (!string.IsNullOrEmpty(stop.id)) s.AddField("id", stop.id);
            if (!string.IsNullOrEmpty(stop.contentId)) s.AddField("contentId", stop.contentId);
            s.AddField("attraction", stop.attraction ?? "");
            s.AddField("role", stop.role ?? "");
            s.AddField("alternates", StringArray(stop.alternates));
            var placement = JSONObject.emptyObject;
            placement.AddField("prefer", stop.placement?.prefer ?? "any");
            s.AddField("placement", placement);
            if (!string.IsNullOrEmpty(stop.options))
            {
                JSONObject options = null;
                try { options = new JSONObject(stop.options); } catch { options = null; }
                if (options != null && options.type == JSONObject.Type.Object) { if (options.count > 0) s.AddField("options", options); }
                else s.AddField("options", stop.options); // kept as text so the validator reports it
            }
            if (!string.IsNullOrEmpty(stop.challenge)) s.AddField("challenge", stop.challenge);
            if (stop.bonus != null && stop.bonus.Count > 0) s.AddField("bonus", StringArray(stop.bonus));
            if (!string.IsNullOrEmpty(stop.displayName)) s.AddField("displayName", stop.displayName);
            if (!string.IsNullOrEmpty(stop.objectiveText)) s.AddField("objectiveText", stop.objectiveText);
            if (!string.IsNullOrEmpty(stop.introHint)) s.AddField("introHint", stop.introHint);
            if (stop.lockedUntil != null && stop.lockedUntil.Count > 0) s.AddField("lockedUntil", StringArray(stop.lockedUntil));
            if (!string.IsNullOrEmpty(stop.lockSign)) s.AddField("lockSign", stop.lockSign);
            return s;
        }

        // ── pass-through: what each object looked like when it was read ──

        sealed class Origin
        {
            public JSONObject raw;    // the object as read (or as last given extras)
            public JSONObject canon;  // what the model made of it at that moment
        }

        static readonly ConditionalWeakTable<object, Origin> Origins = new ConditionalWeakTable<object, Origin>();

        static void Remember(object model, JSONObject raw, JSONObject canon)
        {
            if (model == null || raw == null || raw.type != JSONObject.Type.Object) return;
            Origins.Remove(model);
            Origins.Add(model, new Origin { raw = DeepCopy(raw), canon = canon });
        }

        static Origin OriginOf(object model) => model != null && Origins.TryGetValue(model, out Origin o) ? o : null;

        /// The model's own fields right now (structural lists excluded): what a write would say about it.
        static JSONObject CanonOf(object model)
        {
            switch (model)
            {
                case ContentPlaylistStore.Data d: return CanonRoot(d);
                case PackagePlaylist p: return CanonPlaylist(p, p.titleId);
                case PlaylistLeg l: return CanonLeg(l);
                case PlaylistStop s: return CanonStop(s);
                default: return null;
            }
        }

        static readonly string[] Structural = { "playlists", "arc", "stops" };

        /// <summary>True when the object was read from JSON (its unknown fields will be kept).</summary>
        public static bool HasOrigin(object model) => OriginOf(model) != null;

        /// <summary>The names of the fields the model does not know (they pass through a save): story, scene, goal, lua,
        /// dreamie, timing, campaigns, flags …</summary>
        public static List<string> ExtraKeys(object model)
        {
            Origin o = OriginOf(model);
            JSONObject canon = CanonOf(model);
            if (o == null || canon == null || o.raw.keys == null) return new List<string>();
            return o.raw.keys.Where(k => !Structural.Contains(k) && !KnownKeys.Contains(k) && !canon.HasField(k)).ToList();
        }

        /// <summary>A field the model does not know (e.g. a stop's `goal`, a playlist's `lua` / `dreamie` / `timing`),
        /// as read from the file or set with <see cref="SetExtra"/>; null when absent. Treat it as read-only.</summary>
        public static JSONObject Extra(object model, string key)
        {
            Origin o = OriginOf(model);
            if (o == null || string.IsNullOrEmpty(key) || Structural.Contains(key)) return null;
            JSONObject canon = CanonOf(model);
            if (canon != null && canon.HasField(key)) return null;
            return o.raw.GetField(key);
        }

        /// <summary>Set (or with null, remove) a field the model does not know. It is written on the next save, in its
        /// place, and survives Clone. Throws for a field the model owns (edit the model instead).</summary>
        public static void SetExtra(object model, string key, JSONObject value)
        {
            if (model == null || string.IsNullOrEmpty(key)) return;
            JSONObject canon = CanonOf(model) ?? throw new ArgumentException("not a playlist model: " + model.GetType().Name);
            if (canon.HasField(key) || Structural.Contains(key) || KnownKeys.Contains(key))
                throw new ArgumentException($"'{key}' is a field the Studio edits directly");
            Origin o = OriginOf(model);
            if (o == null)
            {
                Origins.Add(model, o = new Origin { raw = DeepCopy(canon), canon = canon });
            }
            if (value == null) o.raw.RemoveField(key);
            else o.raw.SetField(key, DeepCopy(value));
        }

        /// Fields the model owns at some level (never extras, even when absent/default right now).
        static readonly HashSet<string> KnownKeys = new HashSet<string>
        {
            "schemaVersion", "kinds", "campaigns", "flags", "layout",
            "id", "owner", "titleId", "kind", "name", "pitch", "art", "tags", "badges", "chapter", "requires", "sets", "window",
            "personal", "continueWith", "fit", "rules", "noBonus", "wholeSpaceArena", "roundWord", "one", "many",
            "beat", "minutes", "next", "titles", "mixed", "objective", "objectiveText", "bonusLevels", "bonusBosses",
            "contentId", "attraction", "role", "alternates", "placement", "prefer", "options", "challenge", "bonus", "displayName",
            "introHint", "lockedUntil",
            // record v2 (§ 8)
            "schema", "requiresRelease", "play", "mode", "players", "estMinutes", "lockSign",
        };

        /// raw order first; unchanged fields verbatim, changed ones from the model, unknown ones untouched; then new fields.
        static JSONObject Merge(Origin origin, JSONObject canon, params string[] structural)
        {
            if (origin == null) return canon;
            var result = JSONObject.emptyObject;
            JSONObject raw = origin.raw;
            for (int i = 0; i < raw.count && raw.keys != null; i++)
            {
                string key = raw.keys[i];
                if (structural.Contains(key)) { JSONObject cv = canon.GetField(key); if (cv != null) result.AddField(key, cv); continue; }
                JSONObject now = canon.GetField(key), was = origin.canon?.GetField(key);
                if (Same(now, was)) result.AddField(key, DeepCopy(raw.list[i]));
                else if (now != null && now.type == JSONObject.Type.Object && raw.list[i] != null && raw.list[i].type == JSONObject.Type.Object)
                    result.AddField(key, MergeNested(raw.list[i], was, now));   // rules / fit / objective: keep sub-fields the model lacks
                else if (now != null) result.AddField(key, now);
            }
            for (int i = 0; i < canon.count && canon.keys != null; i++)
            {
                string key = canon.keys[i];
                if (raw.HasField(key)) continue;
                if (structural.Contains(key) || !Same(canon.list[i], origin.canon?.GetField(key))) result.AddField(key, canon.list[i]);
            }
            return result;
        }

        /// A structural copy (JSONObject.Copy() re-parses its own print, which breaks on some values).
        public static JSONObject DeepCopy(JSONObject o)
        {
            if (o == null) return null;
            switch (o.type)
            {
                case JSONObject.Type.Object:
                {
                    var r = JSONObject.emptyObject;
                    for (int i = 0; i < o.count && o.keys != null; i++) r.AddField(o.keys[i], DeepCopy(o.list[i]));
                    return r;
                }
                case JSONObject.Type.Array:
                {
                    var a = JSONObject.emptyArray;
                    for (int i = 0; i < o.count; i++) a.Add(DeepCopy(o.list[i]));
                    return a;
                }
                case JSONObject.Type.String: return JSONObject.CreateStringObject(o.stringValue ?? "");
                case JSONObject.Type.Number: return o.isInteger ? JSONObject.Create(o.longValue) : JSONObject.Create(o.doubleValue);
                case JSONObject.Type.Bool: return JSONObject.Create(o.boolValue);
                default: return JSONObject.nullObject;
            }
        }

        /// A changed nested object (e.g. `rules` edited in the Studio while it also carries `navigation`): sub-fields the
        /// model never wrote are kept from the raw value, the rest follow the model.
        static JSONObject MergeNested(JSONObject raw, JSONObject was, JSONObject now)
        {
            var result = JSONObject.emptyObject;
            for (int i = 0; i < raw.count && raw.keys != null; i++)
            {
                string key = raw.keys[i];
                JSONObject n = now.GetField(key), w = was?.GetField(key);
                if (n == null) { if (w == null) result.AddField(key, DeepCopy(raw.list[i])); continue; }   // unknown: kept; known + gone: removed
                if (Same(n, w)) result.AddField(key, DeepCopy(raw.list[i]));
                else if (n.type == JSONObject.Type.Object && raw.list[i] != null && raw.list[i].type == JSONObject.Type.Object) result.AddField(key, MergeNested(raw.list[i], w, n));
                else result.AddField(key, n);
            }
            for (int i = 0; i < now.count && now.keys != null; i++)
                if (!raw.HasField(now.keys[i])) result.AddField(now.keys[i], now.list[i]);
            return result;
        }

        static bool Same(JSONObject a, JSONObject b)
        {
            if (a == null || b == null) return a == null && b == null;
            return Pretty(a) == Pretty(b);
        }

        public static PackagePlaylist Clone(PackagePlaylist p, string titleId = null)
            => p == null ? null : ReadPlaylist(WritePlaylist(p, titleId ?? p.titleId));

        // ── pretty printer (stable: 2 spaces, scalar arrays on one line) ──

        public static string Pretty(JSONObject o) => Pretty(o, false);

        /// expandScalars: scalar arrays one item per line too (else on one line).
        public static string Pretty(JSONObject o, bool expandScalars)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0, expandScalars);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, JSONObject o, int depth, bool expand = false)
        {
            if (o == null) { sb.Append("null"); return; }
            switch (o.type)
            {
                case JSONObject.Type.Object:
                    if (o.count == 0) { sb.Append("{}"); return; }
                    sb.Append("{\n");
                    for (int i = 0; i < o.count; i++)
                    {
                        Indent(sb, depth + 1);
                        Quote(sb, o.keys[i]);
                        sb.Append(": ");
                        Write(sb, o.list[i], depth + 1, expand);
                        if (i < o.count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    Indent(sb, depth);
                    sb.Append('}');
                    return;
                case JSONObject.Type.Array:
                    if (o.count == 0) { sb.Append("[]"); return; }
                    if (!expand && o.list.All(x => x != null && x.type != JSONObject.Type.Object && x.type != JSONObject.Type.Array))
                    {
                        sb.Append('[');
                        for (int i = 0; i < o.count; i++) { if (i > 0) sb.Append(", "); Write(sb, o.list[i], depth); }
                        sb.Append(']');
                        return;
                    }
                    sb.Append("[\n");
                    for (int i = 0; i < o.count; i++)
                    {
                        Indent(sb, depth + 1);
                        Write(sb, o.list[i], depth + 1, expand);
                        if (i < o.count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    Indent(sb, depth);
                    sb.Append(']');
                    return;
                case JSONObject.Type.String: Quote(sb, o.stringValue ?? ""); return;
                case JSONObject.Type.Bool: sb.Append(o.boolValue ? "true" : "false"); return;
                case JSONObject.Type.Number:
                    if (o.isInteger) sb.Append(o.longValue.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(FormatNumber(o.doubleValue));
                    return;
                default: sb.Append("null"); return;
            }
        }

        static string FormatNumber(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }

        static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

        /// JSON string escaping (Defective keeps stringValue unescaped after a parse; we print it ourselves).
        public static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── small helpers (shared by the Studio core) ──

        internal static IEnumerable<JSONObject> Items(JSONObject array)
            => array?.list != null && array.type == JSONObject.Type.Array
                ? array.list.Where(x => x != null) : Enumerable.Empty<JSONObject>();

        internal static string Str(JSONObject o, string key)
        {
            JSONObject f = o?.GetField(key);
            if (f == null) return null;
            if (f.type == JSONObject.Type.String) return f.stringValue;
            if (f.type == JSONObject.Type.Number) return f.isInteger ? f.longValue.ToString(CultureInfo.InvariantCulture) : FormatNumber(f.doubleValue);
            return null;
        }

        internal static int Int(JSONObject o, string key)
        {
            JSONObject f = o?.GetField(key);
            if (f == null) return 0;
            if (f.type == JSONObject.Type.Number) return f.isInteger ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, f.longValue)) : (int)Math.Round(f.doubleValue);
            if (f.type == JSONObject.Type.String && int.TryParse(f.stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            return 0;
        }

        internal static float Float(JSONObject o, string key)
        {
            JSONObject f = o?.GetField(key);
            if (f == null) return 0f;
            if (f.type == JSONObject.Type.Number) return f.isInteger ? f.longValue : (float)f.doubleValue;
            if (f.type == JSONObject.Type.String && float.TryParse(f.stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
            return 0f;
        }

        internal static bool Bool(JSONObject o, string key, bool fallback)
        {
            JSONObject f = o?.GetField(key);
            return f != null && f.type == JSONObject.Type.Bool ? f.boolValue : fallback;
        }

        internal static List<string> Strings(JSONObject array)
            => Items(array).Where(x => x.type == JSONObject.Type.String && !string.IsNullOrWhiteSpace(x.stringValue))
                .Select(x => x.stringValue).ToList();

        internal static JSONObject StringArray(IEnumerable<string> values)
        {
            var array = JSONObject.emptyArray;
            foreach (string v in values ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(v)) array.Add(v);
            return array;
        }

        internal static void AddNumber(JSONObject o, string key, float value)
        {
            if (Math.Abs(value - Math.Round(value)) < 1e-4) o.AddField(key, (int)Math.Round(value));
            else o.AddField(key, (float)Math.Round(value, 3));
        }
    }
}
#endif
