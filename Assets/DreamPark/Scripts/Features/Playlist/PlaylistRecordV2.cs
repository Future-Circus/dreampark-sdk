// ─────────────────────────────────────────────────────────────────────
//  PlaylistRecordV2.cs — the playlist RECORD v2 (repair-oct20 CONTRACT § 8, Aidan approved).
//
//  Upgrade, don't fork: every reader (the SDK store + compiler, Playlist Studio, the device's
//  backend / inline records) runs a v1 record through Upgrade first, and every writer emits v2:
//    • "schema": "dreampark.playlist/2"
//    • arc[].minutes → arc[].estMinutes (an estimate; hard caps stay in `timing`)
//    • arc[].objective dropped when the runtime derives the same end (a boss round ends on its boss,
//      any other round when every stop is over); a v1 objective the derivation cannot reproduce
//      (a timed / scored round, a count) is KEPT, so a v1 → v2 upgrade never loses behaviour
//    • fit.minPlayers → play.players [min, 16] (accepted on read)
//    • dreamie.beats["layout.<legId>"] → arc[legId].dreamie (guide + its other keys)
//    • dreamie.react[] with `at` = a stop id → that stop's dreamie.react (without `at`)
//  New v2 fields read here: requiresRelease, play {mode, players}, arc[].dreamie {guide, react},
//  stops[].dreamie {guide, intro, react}, stops[].lockSign. Unknown fields always pass through.
//
//  Engine-free: JSONObject + the manifest types only (the SDK's Studio round-trip harness and the
//  offline sims compile it alone).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;

namespace DreamPark
{
    public static class PlaylistRecordV2
    {
        public const string Schema = "dreampark.playlist/2";
        /// play.players max when a v1 record only said fit.minPlayers (the validator's ceiling).
        public const int MaxPlayers = 16;
        public static readonly string[] Modes = { "solo", "coop", "versus", "party" };

        public static bool IsV2(JSONObject p) => p != null && p.type == JSONObject.Type.Object && Str(p, "schema") == Schema;

        /// <summary>v1 → v2 IN PLACE (idempotent; a v2 record comes back unchanged). Returns true when anything moved.</summary>
        public static bool Upgrade(JSONObject p) => Upgrade(p, null);

        /// <param name="notes">optional: one line per kept / moved item (validators turn them into warnings).</param>
        public static bool Upgrade(JSONObject p, List<string> notes)
        {
            if (p == null || p.type != JSONObject.Type.Object) return false;
            bool changed = false;
            if (Str(p, "schema") != Schema)
            {
                if (p.HasField("schema")) p.RemoveField("schema");
                InsertAfter(p, "id", "schema", JSONObject.CreateStringObject(Schema));
                changed = true;
            }
            var arc = p.GetField("arc");
            var legs = arc != null && arc.type == JSONObject.Type.Array ? arc.list : new List<JSONObject>();
            foreach (var leg in legs)
            {
                if (leg == null || leg.type != JSONObject.Type.Object) continue;
                // minutes → estMinutes (same place in the object).
                var min = leg.GetField("minutes");
                if (min != null)
                {
                    if (leg.GetField("estMinutes") == null) Rename(leg, "minutes", "estMinutes");
                    else leg.RemoveField("minutes");
                    changed = true;
                }
                var ob = leg.GetField("objective");
                if (ob != null)
                {
                    if (ob.type != JSONObject.Type.Object || ObjectiveDerivable(leg, ob)) { leg.RemoveField("objective"); changed = true; }
                    else notes?.Add($"round '{Str(leg, "id")}': objective {ob.Print()} kept (the v2 derivation cannot reproduce it)");
                }
            }
            // fit.minPlayers → play.players
            var fit = p.GetField("fit");
            var mp = fit != null && fit.type == JSONObject.Type.Object ? fit.GetField("minPlayers") : null;
            if (mp != null)
            {
                int n = (int)Math.Round(mp.doubleValue);
                fit.RemoveField("minPlayers");
                changed = true;
                if (n > 0 && p.GetField("play") == null)
                {
                    var play = JSONObject.emptyObject;
                    var pl = JSONObject.emptyArray; pl.Add(n); pl.Add(Math.Max(n, MaxPlayers));
                    play.AddField("players", pl);
                    InsertAfter(p, "fit", "play", play);
                }
            }
            // Dreamie guidance colocated with its round / stop.
            var dreamie = p.GetField("dreamie");
            if (dreamie != null && dreamie.type == JSONObject.Type.Object)
            {
                var beats = dreamie.GetField("beats");
                if (beats != null && beats.type == JSONObject.Type.Object && beats.keys != null)
                    foreach (string k in new List<string>(beats.keys))
                    {
                        if (!k.StartsWith("layout.", StringComparison.Ordinal)) continue;
                        string legId = k.Substring("layout.".Length);
                        var leg = legs.Find(l => l != null && l.type == JSONObject.Type.Object && Str(l, "id") == legId);
                        var beat = beats.GetField(k);
                        if (leg == null || beat == null) continue;
                        if (beat.type != JSONObject.Type.String && (beat.type != JSONObject.Type.Object || beat.keys == null)) continue;
                        if (Dreamie(leg, create: false)?.GetField("guide") != null) continue;   // the round already says it: the beat stays
                        var ld = Dreamie(leg, create: true);
                        if (beat.type == JSONObject.Type.String) ld.AddField("guide", beat.stringValue ?? "");
                        else
                        {
                            for (int i = 0; i < beat.keys.Count; i++)
                                if (ld.GetField(beat.keys[i]) == null) ld.AddField(beat.keys[i], beat.list[i]);
                        }
                        beats.RemoveField(k);
                        changed = true;
                    }
                var react = dreamie.GetField("react");
                if (react != null && react.type == JSONObject.Type.Array && react.list != null)
                {
                    var keep = new List<JSONObject>();
                    foreach (var r in react.list)
                    {
                        string at = r != null && r.type == JSONObject.Type.Object && r.GetField("at") is JSONObject a && a.type == JSONObject.Type.String ? a.stringValue : null;
                        var stops = at == null ? null : StopsWithId(legs, at);
                        if (stops == null || stops.Count == 0) { keep.Add(r); continue; }
                        foreach (var s in stops)
                        {
                            var sd = Dreamie(s, create: true);
                            var sr = sd.GetField("react");
                            if (sr == null || sr.type != JSONObject.Type.Array) { sr = JSONObject.emptyArray; sd.SetField("react", sr); }
                            var moved = JSONObject.emptyObject;
                            for (int i = 0; i < r.keys.Count; i++) if (r.keys[i] != "at") moved.AddField(r.keys[i], r.list[i]);
                            sr.Add(moved);
                        }
                        changed = true;
                    }
                    if (keep.Count != react.list.Count) { react.list.Clear(); react.list.AddRange(keep); }
                }
            }
            return changed;
        }

        /// The round end the runtime derives (v2 has no round objective; also the release path's `objective` for older apps,
        /// agreed with Web): the first boss stop, else the first miniboss → defeat_boss at it; a climax with no boss →
        /// survive; a round of only `end` stops → time; otherwise reach_exit with no target (every stop over). Platform
        /// bookends never count as a boss. No count / seconds: with goal blocks a round ends when every stop is over.
        public static PlaylistObjective DeriveObjective(PlaylistLeg leg)
        {
            var o = DeriveCore(leg);
            o.derived = true;
            return o;
        }

        static PlaylistObjective DeriveCore(PlaylistLeg leg)
        {
            PlaylistStop boss = null;
            foreach (string role in new[] { "boss", "miniboss" })
            {
                if (boss != null) break;
                foreach (var s in leg?.stops ?? new List<PlaylistStop>())
                    if (s != null && s.role == role && !PlaylistBookends.IsPlatformStop(s.contentId, s.attraction)) { boss = s; break; }
            }
            if (boss != null) return new PlaylistObjective { type = "defeat_boss", target = boss.attraction };
            if (leg?.beat == "climax") return new PlaylistObjective { type = "survive" };
            var stops = leg?.stops?.FindAll(s => s != null);
            if (stops != null && stops.Count > 0 && stops.TrueForAll(s => s.role == "end")) return new PlaylistObjective { type = "time" };
            return new PlaylistObjective { type = "reach_exit" };
        }

        /// A v1 objective the derivation reproduces (so v2 may drop it): defeat_boss on a round with a boss / miniboss
        /// (no target or a boss stop's attraction), or reach_exit / collect / score with no count, no seconds and no
        /// target outside the round, on a round with no boss.
        public static bool ObjectiveDerivable(JSONObject leg, JSONObject ob)
        {
            if (ob == null || ob.type != JSONObject.Type.Object) return true;
            string type = Str(ob, "type") ?? "", target = Str(ob, "target");
            double count = Num(ob, "count"), seconds = Num(ob, "seconds");
            var stops = leg?.GetField("stops")?.list ?? new List<JSONObject>();
            var bosses = stops.FindAll(s => s != null && (Str(s, "role") == "boss" || Str(s, "role") == "miniboss"));
            if (type == "defeat_boss")
                return bosses.Count > 0 && count <= 0 && seconds <= 0 && (string.IsNullOrEmpty(target) || bosses.Exists(s => Same(Str(s, "attraction"), target)));
            if (type == "reach_exit" || type == "collect" || type == "score" || type == "")
                return bosses.Count == 0 && count <= 0 && seconds <= 0
                    && (string.IsNullOrEmpty(target) || stops.Exists(s => s != null && Same(Str(s, "attraction"), target)));
            return false;
        }

        // ── the guidance a round / stop carries (v2 § 8.5) ───────────────────────────────────────────────

        /// `dreamie` of a round / stop JSON, optionally created.
        public static JSONObject Dreamie(JSONObject o, bool create)
        {
            var d = o?.GetField("dreamie");
            if (d != null && d.type == JSONObject.Type.Object) return d;
            if (!create || o == null) return null;
            d = JSONObject.emptyObject;
            int at = o.keys != null ? o.keys.IndexOf("stops") : -1;   // a round's guidance sits before its stops
            if (at >= 0) { o.keys.Insert(at, "dreamie"); o.list.Insert(at, d); }
            else o.SetField("dreamie", d);
            return d;
        }

        /// The guide for a moment from one `dreamie` block (a round's or a stop's): a react entry `on` = the moment,
        /// else (stop_intro) `intro`, else (layout_appeared / guide_room for a round) `guide`. Null = none.
        public static string GuideFor(string dreamieJson, string moment, bool round)
        {
            if (string.IsNullOrEmpty(dreamieJson)) return null;
            JSONObject d;
            try { d = new JSONObject(dreamieJson); } catch (Exception) { return null; }
            if (d == null || d.type != JSONObject.Type.Object) return null;
            foreach (var r in d.GetField("react")?.list ?? new List<JSONObject>())
                if (r != null && Str(r, "on") == moment && !string.IsNullOrEmpty(Str(r, "guide"))) return Str(r, "guide");
            if (!round && moment == "stop_intro" && !string.IsNullOrEmpty(Str(d, "intro"))) return Str(d, "intro");
            if (round && (moment == "layout_appeared" || moment == "guide_room" || moment == "round_next") && !string.IsNullOrEmpty(Str(d, "guide"))) return Str(d, "guide");
            return null;
        }

        // ── helpers ──

        static List<JSONObject> StopsWithId(List<JSONObject> legs, string id)
        {
            var hits = new List<JSONObject>();
            foreach (var l in legs)
                foreach (var s in l?.GetField("stops")?.list ?? new List<JSONObject>())
                    if (s != null && s.type == JSONObject.Type.Object && Str(s, "id") == id) hits.Add(s);
            return hits;
        }

        static bool Same(string a, string b) => Leaf(a) == Leaf(b) && !string.IsNullOrEmpty(Leaf(a));
        static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        /// Rename a key in place (keeps its position).
        public static void Rename(JSONObject o, string from, string to)
        {
            if (o?.keys == null) return;
            int i = o.keys.IndexOf(from);
            if (i >= 0) o.keys[i] = to;
        }

        /// Add a field right after `after` (or at the end when `after` is absent).
        public static void InsertAfter(JSONObject o, string after, string key, JSONObject value)
        {
            if (o == null) return;
            if (o.keys == null || o.list == null) { o.AddField(key, value); return; }
            int i = o.keys.IndexOf(after);
            if (i < 0) { o.AddField(key, value); return; }
            o.keys.Insert(i + 1, key);
            o.list.Insert(i + 1, value);
        }

        static string Str(JSONObject o, string k)
        {
            var f = o?.GetField(k);
            return f != null && f.type == JSONObject.Type.String ? f.stringValue : null;
        }

        static double Num(JSONObject o, string k)
        {
            var f = o?.GetField(k);
            return f != null && f.type == JSONObject.Type.Number ? f.doubleValue : 0;
        }
    }
}
