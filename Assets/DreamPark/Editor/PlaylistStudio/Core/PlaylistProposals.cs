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
    /// <summary>A generated (or migrated) playlist waiting for the developer: accept, edit or reject.</summary>
    public sealed class PlaylistProposal
    {
        public PackagePlaylist playlist;
        public string source = "generate";      // generate | remix | migrate
        public string state = "new";            // new | accepted | rejected
        public readonly List<string> repairs = new List<string>();   // what post-processing fixed, in words
        public List<PlaylistIssue> issues = new List<PlaylistIssue>();
        public List<PlaylistFitRow> fit = new List<PlaylistFitRow>();
        public string fitProblem;               // why the fit matrix blocks it (null = passes)
        public string designNotes = "";

        public bool HasErrors => issues.Any(i => i.isError);
        /// The save gate (PLAYLISTS.md § 3 validator + the fit matrix).
        public bool CanSave => playlist != null && !HasErrors && fitProblem == null;
        public bool FitsSmallRoom => PlaylistFitCheck.FitsSmallRoom(fit);
    }

    /// <summary>What Web's POST /api/sdk/playlists/generate returned, parsed.</summary>
    public sealed class GeneratorResponse
    {
        public bool success;
        public string error, code, model;
        public List<PlaylistKind> kinds = new List<PlaylistKind>();
        public List<JSONObject> playlists = new List<JSONObject>();
        public List<string> notes = new List<string>();
        /// Web's own checks of each playlist (`{playlistId|null, message}` → "id: message").
        public List<string> warnings = new List<string>();
        public int inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens;

        public static GeneratorResponse Parse(string json)
        {
            var r = new GeneratorResponse();
            JSONObject root = null;
            try { root = string.IsNullOrWhiteSpace(json) ? null : new JSONObject(json); } catch { root = null; }
            if (root == null || root.type != JSONObject.Type.Object) { r.error = "The generator answered with something that is not JSON."; r.code = "BAD_RESPONSE"; return r; }
            r.success = PlaylistStudioJson.Bool(root, "success", false);
            r.error = PlaylistStudioJson.Str(root, "error");
            r.code = PlaylistStudioJson.Str(root, "code");
            r.model = PlaylistStudioJson.Str(root, "model");
            r.kinds = PlaylistStudioJson.ReadKinds(root.GetField("kinds"));
            r.playlists = PlaylistStudioJson.Items(root.GetField("playlists")).Where(p => p.type == JSONObject.Type.Object).ToList();
            r.notes = PlaylistStudioJson.Strings(root.GetField("notes"));
            foreach (JSONObject w in PlaylistStudioJson.Items(root.GetField("warnings")))
            {
                if (w.type == JSONObject.Type.String) { r.warnings.Add(w.stringValue); continue; }
                string pid = PlaylistStudioJson.Str(w, "playlistId"), msg = PlaylistStudioJson.Str(w, "message");
                if (!string.IsNullOrEmpty(msg)) r.warnings.Add(string.IsNullOrEmpty(pid) ? msg : pid + ": " + msg);
            }
            JSONObject usage = root.GetField("usage");
            r.inputTokens = PlaylistStudioJson.Int(usage, "inputTokens");
            r.outputTokens = PlaylistStudioJson.Int(usage, "outputTokens");
            r.cacheReadTokens = PlaylistStudioJson.Int(usage, "cacheReadTokens");
            r.cacheWriteTokens = PlaylistStudioJson.Int(usage, "cacheWriteTokens");
            if (!r.success && string.IsNullOrEmpty(r.error)) r.error = "The generator did not succeed.";
            return r;
        }
    }

    /// <summary>
    /// Generator post-processing (pure): turns the model's playlists into proposals that are repaired where a
    /// fix is mechanical, de-duplicated against the title's playlists and each other, validated with the SAME
    /// validator the uploader runs, and fit-checked with the SAME planner core runs. Only a proposal with no
    /// validator error that plays in every matrix space its `fit` allows can be saved.
    /// </summary>
    public static class PlaylistProposals
    {
        public sealed class Context
        {
            public string titleId;
            public ContentPlaylistStore.Data existing;            // the title's file now (may be null)
            public IReadOnlyList<PlaylistLibraryItem> library = new List<PlaylistLibraryItem>();
            public ISet<string> badgeIds;                          // the title's badge ids (null = unknown, keep all)
            public IEnumerable<PlaylistFitSpace> spaces;           // null = the full matrix
            public string source = "generate";
        }

        public static List<PlaylistProposal> Process(GeneratorResponse response, Context ctx, out List<string> dropped)
            => Process(response.playlists, response.kinds, ctx, out dropped);

        public static List<PlaylistProposal> Process(IEnumerable<JSONObject> raw, IEnumerable<PlaylistKind> responseKinds,
            Context ctx, out List<string> dropped)
        {
            dropped = new List<string>();
            var proposals = new List<PlaylistProposal>();
            List<PlaylistKind> kinds = MergeKinds(ctx.existing, responseKinds);
            var resolve = PlaylistLibrary.Resolver(ctx.library);
            PlaylistFootprintBook book = PlaylistLibrary.Book(ctx.titleId, ctx.library);
            var takenIds = new HashSet<string>((ctx.existing?.playlists ?? new List<PackagePlaylist>()).Select(p => p.id).Where(i => i != null), StringComparer.Ordinal);
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);     // signature → name it belongs to
            foreach (PackagePlaylist p in ctx.existing?.playlists ?? new List<PackagePlaylist>())
                seen[Signature(p)] = p.name;

            foreach (JSONObject o in raw ?? Enumerable.Empty<JSONObject>())
            {
                PackagePlaylist p;
                try { p = PlaylistStudioJson.ReadPlaylist(o); } catch (Exception e) { dropped.Add("unreadable playlist: " + e.Message); continue; }
                if (p == null || p.arc.Count == 0) { dropped.Add($"'{p?.name ?? "?"}' has no arc"); continue; }
                var proposal = new PlaylistProposal { playlist = p, source = ctx.source, designNotes = PlaylistStudioJson.Str(o, "designNotes") ?? "" };
                Repair(proposal, ctx, kinds, resolve, takenIds);
                string sig = Signature(p);
                if (seen.TryGetValue(sig, out string twin)) { dropped.Add($"'{p.name}' repeats '{twin}' stop for stop"); continue; }
                seen[sig] = p.name;
                takenIds.Add(p.id);
                Check(proposal, ctx.titleId, kinds, resolve, book, ctx.spaces);
                proposals.Add(proposal);
            }
            return proposals;
        }

        /// Re-run the gates after the developer edits a proposal.
        public static void Check(PlaylistProposal proposal, string titleId, List<PlaylistKind> kinds,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve, PlaylistFootprintBook book, IEnumerable<PlaylistFitSpace> spaces = null)
        {
            ContentPlaylistStore.Normalize(proposal.playlist);
            var data = new ContentPlaylistStore.Data { kinds = kinds, playlists = new List<PackagePlaylist> { proposal.playlist } };
            proposal.issues = ContentPlaylistValidator.Validate(data, titleId, resolve)
                .Where(i => i.playlistId != null).ToList();
            bool stopsResolve = !proposal.issues.Any(i => i.isError && i.message.Contains("is not an attraction of this title"));
            if (stopsResolve)
            {
                proposal.fit = PlaylistFitCheck.Run(proposal.playlist, titleId, book, spaces);
                proposal.fitProblem = PlaylistFitCheck.Passes(proposal.fit, out string why) ? null : why;
            }
            else
            {
                proposal.fit = new List<PlaylistFitRow>();
                proposal.fitProblem = "not fit-checked: a stop names an attraction this title does not have";
            }
        }

        public static List<PlaylistKind> MergeKinds(ContentPlaylistStore.Data existing, IEnumerable<PlaylistKind> more)
        {
            var kinds = new List<PlaylistKind>();
            foreach (PlaylistKind k in (existing?.kinds ?? new List<PlaylistKind>()).Concat(more ?? Enumerable.Empty<PlaylistKind>()))
                if (k != null && !string.IsNullOrEmpty(k.id) && !kinds.Any(x => x.id == k.id)) kinds.Add(k);
            if (kinds.Count == 0) kinds.Add(PlaylistKind.Default());
            return kinds;
        }

        /// The kinds a saved file needs: the existing ones plus any new kind an accepted playlist uses.
        public static List<PlaylistKind> KindsToSave(ContentPlaylistStore.Data existing, IEnumerable<PlaylistKind> offered, IEnumerable<PackagePlaylist> accepted)
        {
            var kinds = new List<PlaylistKind>((existing?.kinds ?? new List<PlaylistKind>()).Where(k => k != null));
            if (kinds.Count == 0 && accepted.Any(p => p.kind == "adventure")) kinds.Add(PlaylistKind.Default());
            foreach (PackagePlaylist p in accepted)
                if (!kinds.Any(k => k.id == p.kind))
                {
                    PlaylistKind k = (offered ?? Enumerable.Empty<PlaylistKind>()).FirstOrDefault(x => x.id == p.kind)
                        ?? (p.kind == "adventure" ? PlaylistKind.Default() : new PlaylistKind { id = p.kind, singular = PlaylistLibrary.Pretty(p.kind), plural = PlaylistLibrary.Pretty(p.kind) + "s" });
                    kinds.Add(k);
                }
            return kinds;
        }

        // ── repairs: only what is mechanical; meaning is never invented ──

        static readonly Regex NotId = new Regex("[^a-z0-9_-]+");

        public static string Slug(string s)
        {
            string id = NotId.Replace((s ?? "").Trim().ToLowerInvariant().Replace(' ', '-'), "-").Trim('-', '_');
            id = Regex.Replace(id, "-{2,}", "-");
            if (id.Length > 64) id = id.Substring(0, 64).Trim('-', '_');
            return id;
        }

        static void Repair(PlaylistProposal proposal, Context ctx, List<PlaylistKind> kinds,
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve, HashSet<string> takenIds)
        {
            PackagePlaylist p = proposal.playlist;
            var fixes = proposal.repairs;
            if (p.owner != "title") { fixes.Add($"owner '{p.owner}' → title"); p.owner = "title"; }
            if (!string.IsNullOrEmpty(p.titleId) && p.titleId != ctx.titleId) { fixes.Add($"titleId '{p.titleId}' → {ctx.titleId}"); }
            p.titleId = ctx.titleId;

            string id = Slug(string.IsNullOrEmpty(p.id) ? p.name : p.id);
            if (string.IsNullOrEmpty(id)) id = "playlist";
            if (id != p.id) { if (!string.IsNullOrEmpty(p.id)) fixes.Add($"id '{p.id}' → '{id}'"); p.id = id; }
            if (takenIds.Contains(p.id))
            {
                int n = 2; string baseId = p.id.Length > 60 ? p.id.Substring(0, 60) : p.id;
                while (takenIds.Contains(baseId + "-" + n)) n++;
                fixes.Add($"id '{p.id}' is taken → '{baseId}-{n}'");
                p.id = baseId + "-" + n;
            }
            if (!kinds.Any(k => k.id == p.kind))
            {
                string to = kinds[0].id;
                fixes.Add($"kind '{p.kind}' is not one of the title's → '{to}'");
                p.kind = to;
            }
            p.name = Trim(p.name, 60, "name", fixes);
            p.pitch = Trim(p.pitch, 200, "pitch", fixes);
            if (ctx.badgeIds != null)
            {
                var unknown = p.badges.Where(b => !ctx.badgeIds.Contains(b)).ToList();
                if (unknown.Count > 0) { fixes.Add("dropped unknown badge(s) " + string.Join(", ", unknown)); p.badges = p.badges.Where(ctx.badgeIds.Contains).ToList(); }
            }

            var legIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < p.arc.Count; i++)
            {
                PlaylistLeg leg = p.arc[i];
                string legId = Slug(string.IsNullOrEmpty(leg.id) ? "leg" + (i + 1) : leg.id);
                if (string.IsNullOrEmpty(legId)) legId = "leg" + (i + 1);
                while (!legIds.Add(legId)) legId += "-" + (i + 1);
                leg.id = legId;
                leg.objectiveText = Trim(leg.objectiveText, ContentPlaylistValidator.MaxObjectiveText, $"leg '{leg.name}' objectiveText", fixes);
                if (leg.minutes <= 0) { fixes.Add($"leg '{leg.name}' minutes 0 → 1"); leg.minutes = 1; }
                leg.titles = leg.titles.Where(t => t != ctx.titleId).ToList();
                foreach (PlaylistStop stop in leg.stops)
                {
                    if (stop.contentId == ctx.titleId) stop.contentId = null;
                    if (string.IsNullOrEmpty(stop.contentId) && PlaylistBookends.IsKnown(stop.attraction))
                    {
                        stop.contentId = PlaylistBookends.ContentId;
                        stop.attraction = PlaylistBookends.IdOf(stop.attraction);
                    }
                    stop.displayName = Trim(stop.displayName, ContentPlaylistValidator.MaxDisplayName, $"stop '{stop.attraction}' displayName", fixes);
                    stop.objectiveText = Trim(stop.objectiveText, ContentPlaylistValidator.MaxObjectiveText, $"stop '{stop.attraction}' objectiveText", fixes);
                    if (PlaylistBookends.IsPlatformContent(stop.contentId)) { stop.alternates.Clear(); continue; }
                    // A main attraction that is really one of this title's prefabs under another spelling (its resourceName).
                    var found = resolve(stop.attraction);
                    if (found.Count == 1 && found[0].leaf != stop.attraction && resolve(found[0].leaf).Count == 1)
                    {
                        fixes.Add($"stop '{stop.attraction}' → '{found[0].leaf}'");
                        stop.attraction = found[0].leaf;
                    }
                    var badAlts = stop.alternates.Where(a => resolve(a).Count != 1 || a == stop.attraction).ToList();
                    if (badAlts.Count > 0)
                    {
                        fixes.Add($"dropped alternate(s) of '{stop.attraction}' that are not this title's: " + string.Join(", ", badAlts));
                        stop.alternates = stop.alternates.Where(a => !badAlts.Contains(a)).Distinct().ToList();
                    }
                    if (!string.IsNullOrEmpty(stop.options) && System.Text.Encoding.UTF8.GetByteCount(stop.options) > ContentPlaylistValidator.MaxOptionsBytes)
                    { fixes.Add($"dropped options of '{stop.attraction}' (over 2 KB)"); stop.options = ""; }
                }
                if (leg.stops.Count > 0 && leg.stops.All(st => PlaylistBookends.IsPlatformContent(st.contentId)))
                    leg.titles = new List<string> { PlaylistBookends.ContentId };
                // A defeat_boss target that names no stop of the leg falls back to the leg's boss (the runner's own default).
                if (leg.objective != null && !string.IsNullOrEmpty(leg.objective.target)
                    && !leg.stops.Any(s => s.attraction == leg.objective.target || PlaylistBookends.IdOf(s.attraction) == PlaylistBookends.IdOf(leg.objective.target) && PlaylistBookends.IdOf(s.attraction) != null))
                {
                    fixes.Add($"leg '{leg.name}' objective target '{leg.objective.target}' is not one of its stops → cleared");
                    leg.objective.target = null;
                }
            }
            ClampTargets(p, resolve, ctx.library, fixes);
            PlaylistStop first = p.arc[0].stops.FirstOrDefault();
            if (first != null && first.role == "stage") { fixes.Add($"first stop '{first.attraction}' role stage → start"); first.role = "start"; }
        }

        /// The catalog kinds whose target counts what a leg objective counts.
        static readonly Dictionary<string, string[]> ObjectiveKinds = new Dictionary<string, string[]>
        {
            { "score", new[] { "score" } },
            { "collect", new[] { "collect", "catch", "find", "protect", "clear" } },
            { "survive", new[] { "survive" } },
        };

        /// Realistic targets (catalog `targets` {unit, par, max}): a score / collect count or a survive time above what
        /// the stops can give is clamped to the max; a missing one defaults to par. A leg sums its stops (the target
        /// stop alone when it names one). Survive only counts targets in seconds. Every change is listed.
        public static void ClampTargets(PackagePlaylist p, Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve,
            IReadOnlyList<PlaylistLibraryItem> library, List<string> fixes)
        {
            if (library == null || library.Count == 0) return;
            PlaylistLibraryItem ItemOf(string a)
            {
                var r = resolve(a);
                return r.Count == 1 ? library.FirstOrDefault(i => i.guid == r[0].guid) : null;
            }
            foreach (PlaylistLeg leg in p.arc)
            {
                var o = leg.objective;
                if (o == null || !ObjectiveKinds.TryGetValue(o.type ?? "", out string[] kinds)) continue;
                var stops = string.IsNullOrEmpty(o.target) ? leg.stops : leg.stops.Where(st => st.attraction == o.target).ToList();
                var found = new List<PlaylistChallenges.Target>();
                foreach (PlaylistStop st in stops)
                {
                    PlaylistLibraryItem it = ItemOf(st.attraction);
                    if (it == null) continue;
                    string k = kinds.FirstOrDefault(x => it.targets.ContainsKey(x) && (o.type != "survive" || (it.targets[x].unit ?? "").StartsWith("second")));
                    if (k != null) found.Add(it.targets[k]);
                }
                if (found.Count == 0) continue;
                bool capped = found.All(t => t.max > 0);
                float max = capped ? found.Sum(t => t.max) : 0f, par = found.Sum(t => t.par > 0 ? t.par : t.max);
                string unit = found[0].unit;
                int current = o.type == "survive" ? o.seconds : o.count;
                int next = current;
                string why = null;
                if (current <= 0 && par > 0) { next = Mathf.RoundToInt(par); why = $"set to par {next} {unit}"; }
                else if (capped && current > max) { next = Mathf.RoundToInt(max); why = $"{current} is more than the stops can give; clamped to {next} {unit}"; }
                else if (par > 0 && current < par * 0.1f) { next = Mathf.RoundToInt(par); why = $"{current} reads like another unit (par is {par:0.#} {unit}); set to par"; }
                if (next == current) continue;
                fixes.Add($"leg '{leg.name}' {o.type} target " + why);
                if (o.type == "survive") o.seconds = next; else o.count = next;
            }
        }

        static string Trim(string s, int max, string what, List<string> fixes)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            int cut = s.LastIndexOf(' ', Math.Max(0, max - 1));
            string t = (cut > max / 2 ? s.Substring(0, cut) : s.Substring(0, max - 1)).TrimEnd(',', ';', ':', ' ') + "…";
            if (t.Length > max) t = t.Substring(0, max - 1) + "…";
            fixes.Add($"{what} shortened to {max} characters");
            return t;
        }

        /// Two playlists are the same if they play the same stops in the same legs in the same order
        /// (names, kinds and timers aside). Bookend legs do not count.
        public static string Signature(PackagePlaylist p)
            => string.Join(" | ", (p.arc ?? new List<PlaylistLeg>())
                .Where(l => l.stops.Any(s => !PlaylistBookends.IsPlatformStop(s.contentId, s.attraction)))
                .Select(l => string.Join(",", l.stops.Where(s => !PlaylistBookends.IsPlatformStop(s.contentId, s.attraction))
                    .Select(s => s.attraction))));
    }
}
#endif
