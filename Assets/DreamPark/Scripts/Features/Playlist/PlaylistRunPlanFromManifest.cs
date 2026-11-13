using System;
using System.Collections.Generic;

namespace DreamPark
{
    /// <summary>
    /// Manifest playlist (authoring/compiled, PLAYLISTS.md § 4) → the runner's plan for ONE queue item (§ 7.1):
    /// the leg <paramref name="legId"/> of the item's arrangement {playlistId, legId}. Core calls this after the
    /// arrangement's pack commits: <c>place(legIndex, stopIndex, stop)</c> returns the placed stop (occurrence id,
    /// chosen attraction/address, rect, room), or null to keep the compiled occurrence. Kept out of
    /// PlaylistRunRules.cs so the rules stay manifest-free.
    /// </summary>
    public static class PlaylistRunPlanFromManifest
    {
        public static PlaylistRunPlan Build(PackagePlaylist playlist, string legId, string contentId, string instanceId,
            string kindWord, Func<int, int, PlaylistStop, PlaylistRunStop> place = null)
        {
            if (playlist == null) return null;
            var plan = new PlaylistRunPlan
            {
                playlistId = playlist.id, contentId = !string.IsNullOrEmpty(contentId) ? contentId : playlist.titleId,
                instanceId = instanceId, name = playlist.name, kindWord = string.IsNullOrEmpty(kindWord) ? "Adventure" : kindWord,
            };
            if (playlist.badges != null) plan.badges.AddRange(playlist.badges);
            // § 15.3a: the authored sets (full {flags, badges} text wins; else the Studio's flag-id list).
            if (!string.IsNullOrEmpty(playlist.setsJson)) plan.setsJson = playlist.setsJson;
            else if (playlist.sets != null && playlist.sets.Count > 0)
            {
                var sj = new Defective.JSON.JSONObject(Defective.JSON.JSONObject.Type.Array);
                foreach (var f in playlist.sets) if (!string.IsNullOrEmpty(f)) sj.Add(f);
                plan.setsJson = sj.Print();
            }
            var r = playlist.rules ?? new PlaylistRules();
            plan.rules = new PlaylistRunPolicy
            {
                timerSec = r.timerSec, scoring = r.scoring ?? "", lives = r.lives,
                advance = string.IsNullOrEmpty(r.advance) ? PlaylistRunRules.AdvanceComplete : r.advance,
                script = string.IsNullOrEmpty(r.script) ? null : r.script,
                navigation = r.navigation ?? "",
            };
            plan.script = plan.rules.script;
            plan.legIndex = 0;
            // SETS (§ 3.1, pl-sets): a pool + Lua record — the item's leg is the set "set<n>" the run's Lua put out (the run's
            // "set" / this device's registry), set1 = a preview of its on_start.
            if (IsSets(playlist))
            {
                AttachAuthored(playlist, plan);
                PlaylistSets.NotePool(playlist.id, plan.pool);   // the shared set is compact: its goals come back from this pool
                string sid = string.IsNullOrEmpty(legId) || PlaylistSets.SetNumber(legId) <= 0 ? PlaylistSets.SetId(1) : legId;
                var set = PlaylistSets.Resolve(plan, sid);
                if (set == null) { PlaylistSets.Log?.Invoke($"[pl-sets] {playlist.id}: {sid} is not known on this device yet"); return null; }
                var leg = PlaylistSets.Leg(playlist.id, set, place == null ? null : (Func<int, int, PlaylistRunStop, PlaylistRunStop>)((l, i, rs) =>
                    place(l, i, new PlaylistStop { attraction = rs.attraction, resourceAddress = rs.resourceAddress, role = rs.role, attractionId = rs.attractionId, id = rs.stopId })));
                plan.legs.Add(leg);
                plan.legIndex = PlaylistSets.SetNumber(leg.id) - 1;
                return plan;
            }
            for (int l = 0; l < (playlist.arc?.Count ?? 0); l++)
                if (playlist.arc[l] != null && (playlist.arc[l].id == legId || (string.IsNullOrEmpty(playlist.arc[l].id) && legId == "leg" + (l + 1))))
                    plan.legIndex = l;
            var src = new List<(PlaylistStop m, PlaylistRunStop p)>();
            for (int l = 0; l < (playlist.arc?.Count ?? 0); l++)
            {
                var leg = playlist.arc[l];
                if (leg == null) continue;
                var ob = leg.objective ?? new PlaylistObjective();
                var rl = new PlaylistRunLeg
                {
                    id = string.IsNullOrEmpty(leg.id) ? "leg" + (l + 1) : leg.id, name = leg.name, beat = leg.beat,
                    minutes = leg.minutes, next = string.IsNullOrEmpty(leg.next) ? PlaylistRunRules.WalkOn : leg.next,
                    objective = new PlaylistRunObjective { type = ob.type, target = ob.target, count = ob.count, seconds = ob.seconds },
                };
                if (leg.bonusLevels != null) rl.bonusLevels.AddRange(leg.bonusLevels);
                if (playlist.noBonus) rl.noBonus = "noBonus on the playlist";
                else if (leg.noBonus) rl.noBonus = "noBonus on the round";
                for (int i = 0; i < (leg.stops?.Count ?? 0); i++)
                {
                    var s = leg.stops[i];
                    if (s == null) continue;
                    var placed = place?.Invoke(l, i, s) ?? new PlaylistRunStop
                    {
                        occurrenceId = !string.IsNullOrEmpty(s.occurrenceId) ? s.occurrenceId : $"pl-{playlist.id}-{l}-{i}",
                        attraction = Leaf(s.attraction), resourceAddress = s.resourceAddress, role = s.role, standing = true,
                    };
                    if (string.IsNullOrEmpty(placed.role)) placed.role = s.role;
                    if (string.IsNullOrEmpty(placed.contentId) && !string.IsNullOrEmpty(s.contentId) && s.contentId != plan.contentId)
                        placed.contentId = s.contentId;
                    rl.stops.Add(placed);
                    src.Add((s, placed));
                }
                // AUTHORED LOCKS (§ 6.0a): each lockedUntil ref (a sibling's `id` or attraction) → that sibling's occurrence
                // id in THIS round, so a headset leader enforces the lock exactly like the phone's plan.
                foreach (var (ms, rs) in src)
                {
                    if (ms.lockedUntil == null || ms.lockedUntil.Count == 0) continue;
                    rs.lockedUntil = new List<string>(); rs.lockedNeeds = new List<string>();
                    foreach (string want in ms.lockedUntil)
                    {
                        if (string.IsNullOrEmpty(want)) continue;
                        var hit = src.Find(x => x.p != rs && ((!string.IsNullOrEmpty(x.m.id) && x.m.id == want)
                            || Leaf(x.m.attraction) == Leaf(want) || x.p.attraction == Leaf(want)));
                        if (hit.p == null) continue;   // not in this round: never a lock across rounds
                        rs.lockedUntil.Add(hit.p.occurrenceId);
                        rs.lockedNeeds.Add(PlaylistObjectiveText.Name(hit.p));
                    }
                }
                src.Clear();
                plan.legs.Add(rl);
            }
            AttachAuthored(playlist, plan);
            return plan;
        }

        /// repair-oct20 (CONTRACT § 2–§ 3): the playlist's behaviour rides the plan — its Lua, Dreamie script, timing caps,
        /// record rev, and per stop its authored `id` + `goal` (matched by round id and attraction, alternates included).
        /// Every plan builder calls it (this one, core PlaylistPlanBridge.RunPlan), so the leader runs the same blocks.
        /// A pool + Lua record (no arc rounds): its Lua puts the sets out.
        public static bool IsSets(PackagePlaylist p) => p != null && p.pool != null && p.pool.Count > 0 && (p.arc == null || p.arc.Count == 0);

        /// One pool attraction (or an arc stop) as a run stop: the challenge it offers, its options and authored data.
        public static PlaylistRunStop PoolStop(PlaylistStop s, string titleId)
        {
            if (s == null) return null;
            string wish = s.placement?.prefer;
            return new PlaylistRunStop
            {
                attraction = Leaf(!string.IsNullOrEmpty(s.attraction) ? s.attraction : !string.IsNullOrEmpty(s.resourceName) ? s.resourceName : s.attractionId),
                resourceAddress = s.resourceAddress, role = string.IsNullOrEmpty(s.role) ? "stage" : s.role,
                contentId = !string.IsNullOrEmpty(s.contentId) && s.contentId != titleId ? s.contentId : null,
                challenge = string.IsNullOrEmpty(s.challenge) ? null : s.challenge, options = string.IsNullOrEmpty(s.options) ? null : s.options,
                stopId = string.IsNullOrEmpty(s.id) ? null : s.id, goal = string.IsNullOrWhiteSpace(s.goal) || s.goal.Trim() == "default" ? null : s.goal,
                displayName = string.IsNullOrEmpty(s.displayName) ? null : s.displayName, objectiveText = string.IsNullOrEmpty(s.objectiveText) ? null : s.objectiveText,
                introHint = string.IsNullOrEmpty(s.introHint) ? null : s.introHint, lockSign = string.IsNullOrEmpty(s.lockSign) ? null : s.lockSign,
                dreamieJson = string.IsNullOrEmpty(s.dreamieJson) ? null : s.dreamieJson, attractionId = string.IsNullOrEmpty(s.attractionId) ? null : s.attractionId,
                place = wish == "biggest" || wish == "entrance" || wish == "outdoor" ? wish : null,
            };
        }

        public static void AttachAuthored(PackagePlaylist playlist, PlaylistRunPlan plan)
        {
            if (playlist == null || plan == null) return;
            // pl-sets: the pool (an arc[] record's stops serve as its pool), sets mode, requires.badges.
            plan.sets = IsSets(playlist);
            plan.pool.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ps in plan.sets ? playlist.pool : AllArcStops(playlist))
            {
                var rs = PoolStop(ps, playlist.titleId ?? plan.contentId);
                if (rs != null && seen.Add(PlaylistSets.RefOf(rs) + "|" + rs.attraction)) plan.pool.Add(rs);
            }
            plan.requiresBadges.Clear();
            Defective.JSON.JSONObject rq = null;
            try { if (!string.IsNullOrWhiteSpace(playlist.requiresJson)) rq = new Defective.JSON.JSONObject(playlist.requiresJson); } catch (Exception) { }
            plan.requiresBadges.AddRange(PlaylistSets.RequiredBadges(rq, playlist.requires));
            if (!string.IsNullOrWhiteSpace(playlist.lua)) plan.lua = playlist.lua;
            if (!string.IsNullOrWhiteSpace(playlist.dreamieJson)) plan.dreamieJson = playlist.dreamieJson;
            if (!string.IsNullOrWhiteSpace(playlist.timingJson)) plan.timingJson = playlist.timingJson;
            if (!string.IsNullOrWhiteSpace(playlist.rev)) plan.rev = playlist.rev;
            if (playlist.roundWord != null && !string.IsNullOrWhiteSpace(playlist.roundWord.one)) plan.roundWord = playlist.roundWord;
            // Record v2 (CONTRACT § 8): the release it needs, how the group plays it, the colocated Dreamie guidance + lock signs.
            if (playlist.requiresRelease > 0) plan.requiresRelease = playlist.requiresRelease;
            if (playlist.play != null && !playlist.play.IsEmpty) plan.play = playlist.play;
            if (playlist.arc == null) return;
            for (int l = 0; l < plan.legs.Count; l++)
            {
                var rl = plan.legs[l];
                if (rl == null) continue;
                PlaylistLeg al = playlist.arc.Find(x => x != null && !string.IsNullOrEmpty(x.id) && x.id == rl.id)
                    ?? (l < playlist.arc.Count ? playlist.arc[l] : null);
                if (al == null) continue;
                if (string.IsNullOrEmpty(rl.dreamieJson) && !string.IsNullOrWhiteSpace(al.dreamieJson)) rl.dreamieJson = al.dreamieJson;
                if (al.stops == null) continue;
                var used = new HashSet<PlaylistStop>();
                foreach (var rs in rl.stops)
                {
                    if (rs == null) continue;
                    string leaf = Leaf(rs.attraction);
                    PlaylistStop hit = null;
                    foreach (var a in al.stops)
                    {
                        if (a == null || used.Contains(a)) continue;
                        bool same = Leaf(a.attraction) == leaf || Leaf(a.resourceName) == leaf || (a.alternates != null && a.alternates.Exists(x => Leaf(x) == leaf));
                        if (same) { hit = a; break; }
                    }
                    if (hit == null) continue;
                    used.Add(hit);
                    if (string.IsNullOrEmpty(rs.stopId) && !string.IsNullOrEmpty(hit.id)) rs.stopId = hit.id;
                    if (string.IsNullOrEmpty(rs.goal) && !string.IsNullOrWhiteSpace(hit.goal) && hit.goal.Trim() != "default") rs.goal = hit.goal;
                    if (string.IsNullOrEmpty(rs.challenge) && !string.IsNullOrEmpty(hit.challenge)) rs.challenge = hit.challenge;
                    if (string.IsNullOrEmpty(rs.lockSign) && !string.IsNullOrWhiteSpace(hit.lockSign)) rs.lockSign = hit.lockSign;
                    if (string.IsNullOrEmpty(rs.dreamieJson) && !string.IsNullOrWhiteSpace(hit.dreamieJson)) rs.dreamieJson = hit.dreamieJson;
                }
            }
        }

        static IEnumerable<PlaylistStop> AllArcStops(PackagePlaylist p)
        {
            foreach (var l in p?.arc ?? new List<PlaylistLeg>())
                foreach (var s in l?.stops ?? new List<PlaylistStop>()) if (s != null) yield return s;
        }

        /// A curated (owner "platform") playlist as the Web / queue sends it inline (PLAYLISTS.md § 5 shape:
        /// {id, owner, titleId, kind, name, badges, rules, arc:[{id, name, beat, minutes, next, objective,
        /// stops:[{contentId, attractionId, resourceName, attraction?, role}]}]}) → the manifest type, so followers
        /// build their plan exactly like a title playlist. Null when it is not a playlist.
        public static PackagePlaylist ParseInline(Defective.JSON.JSONObject j)
        {
            if (j == null || j.type != Defective.JSON.JSONObject.Type.Object) return null;
            // Record v2 (CONTRACT § 8): a v1 record (no `schema`) is upgraded on read — on a copy, the caller's stays as sent.
            if (!PlaylistRecordV2.IsV2(j)) { j = new Defective.JSON.JSONObject(j.Print()); PlaylistRecordV2.Upgrade(j); }
            string S(Defective.JSON.JSONObject o, string k, string fb = "") => PlaylistRunPlan.Str(o, k, fb);
            var p = new PackagePlaylist
            {
                id = S(j, "id", S(j, "playlistId")), owner = S(j, "owner", "platform"), titleId = S(j, "titleId", null),
                kind = S(j, "kind", "adventure"), name = S(j, "name"), pitch = S(j, "pitch"),
            };
            if (string.IsNullOrEmpty(p.id)) return null;
            var badges = j.GetField("badges");
            if (badges?.list != null) foreach (var b in badges.list) if (b != null && b.type == Defective.JSON.JSONObject.Type.String) p.badges.Add(b.stringValue);
            var sets = j.GetField("sets");
            if (sets != null && (sets.type == Defective.JSON.JSONObject.Type.Object || sets.type == Defective.JSON.JSONObject.Type.Array)) p.setsJson = sets.Print();
            // The suggested next playlists of this title (offline-next: the headset's Next up when the campaign names none).
            var cw = j.GetField("continueWith");
            if (cw?.list != null) foreach (var x in cw.list) if (x != null && x.type == Defective.JSON.JSONObject.Type.String && !string.IsNullOrEmpty(x.stringValue)) p.continueWith.Add(x.stringValue);
            var r = j.GetField("rules");
            if (r != null && r.type == Defective.JSON.JSONObject.Type.Object)
                p.rules = new PlaylistRules
                {
                    timerSec = (int)PlaylistRunPlan.Num(r, "timerSec"), scoring = S(r, "scoring"), lives = (int)PlaylistRunPlan.Num(r, "lives"),
                    advance = S(r, "advance", "on_complete"), script = S(r, "script"), navigation = S(r, "navigation"),
                };
            // repair-oct20: the record's behaviour (CONTRACT § 5) — lua, dreamie, timing, rev — and the record verbatim.
            p.lua = S(j, "lua", null);
            var dj = j.GetField("dreamie"); if (dj != null && dj.type == Defective.JSON.JSONObject.Type.Object) p.dreamieJson = dj.Print();
            var tj = j.GetField("timing"); if (tj != null && tj.type == Defective.JSON.JSONObject.Type.Object) p.timingJson = tj.Print();
            var rv = j.GetField("rev"); if (rv != null) p.rev = rv.type == Defective.JSON.JSONObject.Type.String ? rv.stringValue : rv.Print();
            p.sourceJson = j.Print();
            p.roundWord = PlaylistRunRules.ParseRoundWord(j.GetField("roundWord"));
            p.schema = S(j, "schema", PlaylistRecordV2.Schema);
            p.requiresRelease = (int)PlaylistRunPlan.Num(j, "requiresRelease");
            p.play = PlaylistRunRules.ParsePlay(j.GetField("play")) ?? new PlaylistPlay();
            // pl-sets: the attraction pool (a sets playlist) and requires {badges} / the v1 refs.
            var rq = j.GetField("requires");
            if (rq != null && rq.type == Defective.JSON.JSONObject.Type.Object) p.requiresJson = rq.Print();
            else if (rq?.list != null) foreach (var x in rq.list) if (x != null && x.type == Defective.JSON.JSONObject.Type.String) p.requires.Add(x.stringValue);
            foreach (var sj in j.GetField("pool")?.list ?? new List<Defective.JSON.JSONObject>())
            {
                var ps = StopOf(sj, p.titleId);
                if (ps != null) p.pool.Add(ps);
            }
            var arc = j.GetField("arc");
            if (arc?.list == null) return p.pool.Count > 0 ? p : null;
            foreach (var lj in arc.list)
            {
                if (lj == null || lj.type != Defective.JSON.JSONObject.Type.Object) continue;
                var leg = new PlaylistLeg
                {
                    id = S(lj, "id"), name = S(lj, "name"), beat = S(lj, "beat"),
                    minutes = (int)Math.Round(PlaylistRunPlan.Num(lj, "estMinutes", PlaylistRunPlan.Num(lj, "minutes"))),
                    next = S(lj, "next", "walk_on"),
                };
                var ldj = lj.GetField("dreamie"); if (ldj != null && ldj.type == Defective.JSON.JSONObject.Type.Object) leg.dreamieJson = ldj.Print();
                var oj = lj.GetField("objective");
                if (oj != null && oj.type == Defective.JSON.JSONObject.Type.Object)
                    leg.objective = new PlaylistObjective
                    {
                        type = S(oj, "type", "reach_exit"), target = S(oj, "target", null),
                        count = (int)PlaylistRunPlan.Num(oj, "count"), seconds = (int)PlaylistRunPlan.Num(oj, "seconds"),
                    };
                leg.objectiveText = S(lj, "objectiveText", null);
                var sc = lj.GetField("scene"); if (sc != null && sc.type == Defective.JSON.JSONObject.Type.Object) leg.sceneJson = sc.Print();
                var ch = lj.GetField("choice"); if (ch != null && ch.type == Defective.JSON.JSONObject.Type.Object) leg.choiceJson = ch.Print();
                var titles = lj.GetField("titles");
                if (titles?.list != null) foreach (var t in titles.list) if (t != null && t.type == Defective.JSON.JSONObject.Type.String) leg.titles.Add(t.stringValue);
                var stops = lj.GetField("stops");
                if (stops?.list != null)
                    foreach (var sj in stops.list)
                    {
                        if (sj == null || sj.type != Defective.JSON.JSONObject.Type.Object) continue;
                        var stop = StopOf(sj, p.titleId);
                        if (stop == null) continue;
                        leg.stops.Add(stop);
                    }
                // § 8.7: a v2 round has no objective — the runner derives one (a v1 objective the upgrade kept wins).
                if (leg.objective == null || string.IsNullOrEmpty(leg.objective.type)) leg.objective = PlaylistRecordV2.DeriveObjective(leg);
                p.arc.Add(leg);
            }
            return p.arc.Count > 0 || p.pool.Count > 0 ? p : null;
        }

        /// One record stop / pool attraction JSON → the manifest type.
        static PlaylistStop StopOf(Defective.JSON.JSONObject sj, string titleId)
        {
            if (sj == null || sj.type != Defective.JSON.JSONObject.Type.Object) return null;
            string S(Defective.JSON.JSONObject o, string k, string fb = "") => PlaylistRunPlan.Str(o, k, fb);
            string resourceName = S(sj, "resourceName");
            string attraction = S(sj, "attraction", "");
            if (string.IsNullOrEmpty(attraction)) attraction = !string.IsNullOrEmpty(resourceName) ? Leaf(resourceName) : S(sj, "attractionId");
            var stop = new PlaylistStop
            {
                contentId = S(sj, "contentId", titleId), attraction = attraction, role = S(sj, "role", "stage"),
                resourceName = resourceName, attractionId = S(sj, "attractionId"), resourceAddress = S(sj, "resourceAddress", null),
                occurrenceId = S(sj, "occurrenceId", null),
                id = S(sj, "id", null), challenge = S(sj, "challenge", null),
                goal = sj.GetField("goal") is Defective.JSON.JSONObject g && g.type == Defective.JSON.JSONObject.Type.Object ? S(g, "lua", null)
                     : S(sj, "goal", null),
                displayName = S(sj, "displayName", null), objectiveText = S(sj, "objectiveText", null), introHint = S(sj, "introHint", null),
                lockSign = S(sj, "lockSign", null),
            };
            var sdj = sj.GetField("dreamie"); if (sdj != null && sdj.type == Defective.JSON.JSONObject.Type.Object) stop.dreamieJson = sdj.Print();
            var op = sj.GetField("options");
            if (op != null && op.type == Defective.JSON.JSONObject.Type.Object) stop.options = op.Print();
            foreach (var (f, list) in new[] { ("lockedUntil", stop.lockedUntil), ("alternates", stop.alternates), ("bonus", stop.bonus) })
            {
                var arr = sj.GetField(f);
                if (arr?.list != null) foreach (var x in arr.list) if (x != null && x.type == Defective.JSON.JSONObject.Type.String) list.Add(x.stringValue);
            }
            var pl = sj.GetField("placement") ?? sj.GetField("place");
            string wish = pl != null && pl.type == Defective.JSON.JSONObject.Type.Object ? S(pl, "prefer", null) : pl != null && pl.type == Defective.JSON.JSONObject.Type.String ? pl.stringValue : null;
            if (!string.IsNullOrEmpty(wish)) stop.placement = new PlaylistPlacement { prefer = wish };
            return stop;
        }

        static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }
    }
}
