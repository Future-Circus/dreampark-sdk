#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DreamPark.Editor;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// Playlists (Docs/PLAYLISTS.md): the Content Uploader's minimal Playlists
    /// section. It lists the title's .dreampark-playlists.json, validates it
    /// and shows the errors. Authoring is hand-edited JSON for now. Also builds
    /// the release's packages.playlists block for Web.
    /// </summary>
    public partial class ContentUploaderPanel
    {
        private const string PlaylistsFoldPrefKey = "DreamPark.ContentUploader.PlaylistsFold";
        private bool playlistsFold = true;
        private bool playlistsFoldLoaded;
        private string playlistsCacheKey;
        private ContentPlaylistStore.Data playlistsData;
        private string playlistsLoadError;
        private List<PlaylistIssue> playlistsIssues = new List<PlaylistIssue>();

        private void RefreshPlaylistsIfChanged(bool force)
        {
            string key = contentId + "|" + (ContentPlaylistStore.Exists(contentId)
                ? File.GetLastWriteTimeUtc(ContentPlaylistStore.FullPath(contentId)).Ticks.ToString() : "none");
            if (!force && key == playlistsCacheKey) return;
            playlistsCacheKey = key;
            playlistsData = null;
            playlistsLoadError = null;
            playlistsIssues = new List<PlaylistIssue>();
            try
            {
                playlistsData = ContentPlaylistStore.Load(contentId);
                if (playlistsData != null)
                    playlistsIssues = ContentPlaylistValidator.Validate(playlistsData, contentId,
                        ContentPlaylistValidator.AssetResolver(contentId));
            }
            catch (Exception e) { playlistsLoadError = e.Message; }
        }

        private void DrawPlaylistsSection()
        {
            if (string.IsNullOrEmpty(contentId)) return;
            if (!playlistsFoldLoaded)
            {
                playlistsFold = EditorPrefs.GetBool(PlaylistsFoldPrefKey, true);
                playlistsFoldLoaded = true;
            }
            RefreshPlaylistsIfChanged(false);
            int count = playlistsData?.playlists?.Count ?? 0;
            Rect headerRect = GUILayoutUtility.GetRect(0f,
                EditorGUIUtility.singleLineHeight + 4f, GUILayout.ExpandWidth(true));
            bool expanded = DrawOrganizerSectionHeader(headerRect, headerRect, playlistsFold,
                count > 0 ? $"Playlists ({count})" : "Playlists");
            if (expanded != playlistsFold)
            {
                playlistsFold = expanded;
                EditorPrefs.SetBool(PlaylistsFoldPrefKey, playlistsFold);
            }
            if (!playlistsFold) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("PLAYLISTS are complete adventures with an arc: a start, a climax (a boss) and a real "
                + "ending. Dreamie previews and plays them. Edit " + ContentPlaylistStore.AssetPath(contentId)
                + " (schema: Assets/DreamPark/Docs/PLAYLISTS.md). A playlist with errors is left out of the upload; "
                + "the rest of the title uploads normally.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Playlist Studio", GUILayout.Width(150f)))
                DreamPark.Editor.PlaylistStudio.PlaylistStudioWindow.Open(contentId);
            if (GUILayout.Button("Validate", GUILayout.Width(90f))) RefreshPlaylistsIfChanged(true);
            if (ContentPlaylistStore.Exists(contentId) && GUILayout.Button("Open file", GUILayout.Width(90f)))
                EditorUtility.OpenWithDefaultApp(ContentPlaylistStore.FullPath(contentId));
            if (ContentPlaylistStore.Exists(contentId) && GUILayout.Button("Reveal", GUILayout.Width(70f)))
                EditorUtility.RevealInFinder(ContentPlaylistStore.FullPath(contentId));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (playlistsLoadError != null)
                EditorGUILayout.HelpBox(playlistsLoadError, MessageType.Error);
            else if (playlistsData == null || playlistsData.playlists.Count == 0)
                DrawNoPlaylistsFix();
            else
            {
                List<PlaylistKind> kinds = ContentPlaylistStore.KindsOrDefault(playlistsData);
                GUILayout.Label("Kinds: " + string.Join(", ", kinds.Select(k => $"{k.plural} ({k.id})")),
                    EditorStyles.miniLabel);
                foreach (PlaylistIssue issue in playlistsIssues.Where(i => i.playlistId == null))
                    EditorGUILayout.HelpBox(issue.message, issue.isError ? MessageType.Error : MessageType.Warning);
                foreach (PackagePlaylist playlist in playlistsData.playlists)
                    DrawPlaylistRow(playlist, kinds);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawPlaylistRow(PackagePlaylist playlist, List<PlaylistKind> kinds)
        {
            var mine = playlistsIssues.Where(i => i.playlistId == (string.IsNullOrEmpty(playlist.id) ? "(no id)" : playlist.id)).ToList();
            bool ok = !mine.Any(i => i.isError);
            PlaylistKind kind = kinds.FirstOrDefault(k => k.id == playlist.kind);
            int minutes = playlist.arc.Sum(leg => Math.Max(0, leg.minutes));
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var title = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            GUILayout.Label((ok ? "✓ " : "✕ ") + (playlist.name ?? "(no name)")
                + $"   {kind?.singular ?? playlist.kind} · {minutes} min · {playlist.arc.Count} legs"
                + (ok ? "" : "   NOT UPLOADED"), title);
            if (!string.IsNullOrEmpty(playlist.pitch))
                GUILayout.Label(playlist.pitch, EditorStyles.wordWrappedMiniLabel);
            string arc = string.Join("  →  ", playlist.arc.Select(leg =>
                $"{(leg.beat ?? "?").ToUpperInvariant()}: {leg.name} [{leg.objective?.type}]"
                + (leg.next == "swap_in_place" ? " ⟳" : "")));
            GUILayout.Label(arc, EditorStyles.wordWrappedMiniLabel);
            if (playlist.badges.Count > 0)
                GUILayout.Label("Badges: " + string.Join(", ", playlist.badges), EditorStyles.miniLabel);
            foreach (PlaylistIssue issue in mine)
                EditorGUILayout.HelpBox(issue.message, issue.isError ? MessageType.Error : MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        /// "Arrives complete" (Aidan, Oct 18): a title without playlists still uploads (legacy packages), but the
        /// panel says so plainly and offers the one-click fixes. Never a modal: agents drive this panel too.
        private void DrawNoPlaylistsFix()
        {
            EditorGUILayout.HelpBox("This title has NO PLAYLISTS. Players will only get the legacy Arena / Sequence / Adventure "
                + "packages, with no adventures to pick, no Dreamie tour and no campaign. Generate a complete set, or convert your "
                + "organizers, then review and Save in the Playlist Studio.", MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("✨ Generate playlists", GUILayout.Height(24f)))
                DreamPark.Editor.PlaylistStudio.PlaylistStudioWindow.Open(contentId, DreamPark.Editor.PlaylistStudio.PlaylistStudioWindow.GenerateTab);
            if (GUILayout.Button("Convert my organizers", GUILayout.Height(24f)))
            {
                var w = DreamPark.Editor.PlaylistStudio.PlaylistStudioWindow.Open(contentId, DreamPark.Editor.PlaylistStudio.PlaylistStudioWindow.GenerateTab);
                w.ConvertOrganizersNow();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Release summary (Launch actions): playlists, chains, challenges and fit before an upload ──
        private string releaseSummaryKey;
        private string releaseSummaryText;
        private bool releaseSummaryOk;

        private void DrawPlaylistsReleaseSummary()
        {
            if (string.IsNullOrEmpty(contentId)) return;
            RefreshPlaylistsIfChanged(false);
            int count = playlistsData?.playlists?.Count ?? 0;
            if (count == 0)
            {
                GUILayout.Label("Playlists in this release", EditorStyles.boldLabel);
                DrawNoPlaylistsFix();
                return;
            }
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Playlists in this release", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(releaseSummaryKey == playlistsCacheKey ? "Re-check fit" : "Check fit", EditorStyles.miniButton, GUILayout.Width(90f)))
                BuildReleaseSummary();
            if (GUILayout.Button("Open Studio", EditorStyles.miniButton, GUILayout.Width(90f)))
                DreamPark.Editor.PlaylistStudio.PlaylistStudioWindow.Open(contentId);
            EditorGUILayout.EndHorizontal();
            int valid = playlistsData.playlists.Count(p => ContentPlaylistValidator.IsValid(string.IsNullOrEmpty(p.id) ? "(no id)" : p.id, playlistsIssues));
            int starters = playlistsData.playlists.Count(DreamPark.Editor.PlaylistStudio.PlaylistGates.IsStarter);
            int chained = playlistsData.playlists.Count(p => p.continueWith.Count > 0 || !DreamPark.Editor.PlaylistStudio.PlaylistGates.IsStarter(p));
            GUILayout.Label($"{valid} of {count} ship ({count - valid} left out for errors) · {starters} open from the start · {chained} chained "
                + $"· {playlistsData.playlists.Sum(p => p.arc.Sum(l => l.stops.Count))} stops", EditorStyles.wordWrappedMiniLabel);
            if (releaseSummaryKey == playlistsCacheKey && releaseSummaryText != null)
                EditorGUILayout.HelpBox(releaseSummaryText, releaseSummaryOk ? MessageType.Info : MessageType.Warning);
            else GUILayout.Label("Fit and challenge coverage: press Check fit (runs the packer over six spaces).", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void BuildReleaseSummary()
        {
            try
            {
                var report = DreamPark.Editor.PlaylistStudio.PlaylistStudioHeadless.Check(contentId, out bool ok);
                JSONObject summary = report.GetField("summary");
                int n = summary?.GetField("playlists")?.intValue ?? 0;
                int small = summary?.GetField("fitSmallRoom")?.intValue ?? 0;
                int validFit = summary?.GetField("valid")?.intValue ?? 0;
                int used = summary?.GetField("attractionsInPlaylists")?.intValue ?? 0, all = summary?.GetField("attractions")?.intValue ?? 0;
                var kinds = new HashSet<string>();
                foreach (JSONObject p in report.GetField("playlists")?.list ?? new List<JSONObject>())
                    foreach (JSONObject c in p.GetField("challenges")?.list ?? new List<JSONObject>()) kinds.Add(c.stringValue);
                releaseSummaryText = $"{validFit} of {n} pass validation + fit · {small} play in a 12×18 ft room · {used} of {all} attractions used · "
                    + $"challenges: {string.Join(", ", kinds.Select(DreamPark.Editor.PlaylistStudio.PlaylistChallenges.Name))}"
                    + (summary?.GetField("challengeCatalog")?.boolValue == true ? "" : " (no challenge catalog: guessed from scripts)");
                releaseSummaryOk = ok;
            }
            catch (Exception e) { releaseSummaryText = "Check failed: " + e.Message; releaseSummaryOk = false; }
            releaseSummaryKey = playlistsCacheKey;
        }

        /// Called when an upload starts: a clear console warning (never a modal, never a block).
        private void WarnIfNoPlaylists()
        {
            RefreshPlaylistsIfChanged(false);
            if ((playlistsData?.playlists?.Count ?? 0) == 0)
                Debug.LogWarning($"[Playlists] {contentId} is uploading WITHOUT playlists: players get only the legacy Arena/Sequence/Adventure packages. "
                    + "Content Uploader → Playlists → Generate playlists (or Convert my organizers) to arrive complete.");
        }

        /// packages.playlists for Web (PLAYLISTS.md § 5): ContentPlaylistPublish (no editor state — testable offline).
        internal static JSONObject BuildPublishedPlaylistsJson(DreamParkPackageManifest manifest) => ContentPlaylistPublish.Build(manifest);

        private static JSONObject StringArray(IEnumerable<string> values)
        {
            var array = new JSONObject(JSONObject.Type.Array);
            foreach (string value in values ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(value)) array.Add(value);
            return array;
        }
    }

    /// The playlists block an upload publishes (packages.playlists, PLAYLISTS.md § 5). Plain data in, JSON out: no editor
    /// state, so the offline publish check runs it on real title files (Tools/playlist-run-offline/publish-check).
    public static class ContentPlaylistPublish
    {
        /// <summary>
        /// packages.playlists for Web (PLAYLISTS.md § 5), from the COMPILED
        /// manifest (only valid playlists are there). Null when there are none.
        /// </summary>
        public static JSONObject Build(DreamParkPackageManifest manifest)
        {
            if (ReferenceEquals(manifest, null) || !manifest.HasPlaylists) return null;   // (not Unity's ==: the offline check's manifest has no native object)
            var block = new JSONObject(JSONObject.Type.Object);
            block.AddField("schemaVersion", manifest.playlistsSchemaVersion);
            var kinds = new JSONObject(JSONObject.Type.Array);
            foreach (PlaylistKind kind in manifest.PlaylistKindsOrDefault())
            {
                var k = new JSONObject(JSONObject.Type.Object);
                k.AddField("id", kind.id);
                k.AddField("singular", kind.singular);
                k.AddField("plural", kind.plural);
                kinds.Add(k);
            }
            block.AddField("kinds", kinds);
            // § 15.1 title campaigns / flags (cleaned by the validator); only when authored.
            if (!string.IsNullOrEmpty(manifest.playlistCampaignsJson)) if (Parse(manifest.playlistCampaignsJson) is JSONObject b_campaigns) block.AddField("campaigns", b_campaigns);
            if (!string.IsNullOrEmpty(manifest.playlistFlagsJson)) if (Parse(manifest.playlistFlagsJson) is JSONObject b_flags) block.AddField("flags", b_flags);
            // § 6.0a the title's layout: "club" = one stable layout of its attractions per space (DreamSports).
            if (manifest.IsClub) block.AddField("layout", "club");
            var list = new JSONObject(JSONObject.Type.Array);
            foreach (PackagePlaylist playlist in manifest.playlists)
            {
                if (playlist == null) continue;
                var p = new JSONObject(JSONObject.Type.Object);
                p.AddField("id", playlist.id);
                p.AddField("owner", string.IsNullOrEmpty(playlist.owner) ? "title" : playlist.owner);
                p.AddField("titleId", playlist.titleId ?? manifest.contentId);
                p.AddField("kind", playlist.kind);
                p.AddField("name", playlist.name);
                p.AddField("pitch", playlist.pitch ?? "");
                p.AddField("tags", StringArray(playlist.tags));
                p.AddField("badges", StringArray(playlist.badges));
                // Chapters & unlocks (PLAYLISTS.md § 2.1, proposal): only when authored; old Web ignores them.
                if (!string.IsNullOrEmpty(playlist.chapter)) p.AddField("chapter", playlist.chapter);
                // § 15.1 gates: the full-fidelity text when the file authored it, else the Studio's refs (Web reads both).
                if (!string.IsNullOrEmpty(playlist.requiresJson) && Parse(playlist.requiresJson) is JSONObject g_requires) p.AddField("requires", g_requires);
                else if (playlist.requires != null && playlist.requires.Count > 0) p.AddField("requires", StringArray(playlist.requires));
                if (!string.IsNullOrEmpty(playlist.setsJson) && Parse(playlist.setsJson) is JSONObject g_sets) p.AddField("sets", g_sets);
                else if (playlist.sets != null && playlist.sets.Count > 0) p.AddField("sets", StringArray(playlist.sets));
                if (!string.IsNullOrEmpty(playlist.windowJson) && Parse(playlist.windowJson) is JSONObject g_window) p.AddField("window", g_window);
                if (!string.IsNullOrEmpty(playlist.personalJson) && Parse(playlist.personalJson) is JSONObject g_personal) p.AddField("personal", g_personal);
                if (playlist.continueWith != null && playlist.continueWith.Count > 0) p.AddField("continueWith", StringArray(playlist.continueWith));
                if (playlist.wholeSpaceArena) p.AddField("wholeSpaceArena", true);   // § 6.0: its filled rooms are the arena, not "Bonus"
                var fit = new JSONObject(JSONObject.Type.Object);
                fit.AddField("minRooms", playlist.fit?.minRooms ?? 0);
                fit.AddField("minAreaM2", playlist.fit?.minAreaM2 ?? 0f);
                fit.AddField("indoor", playlist.fit?.indoor ?? true);
                fit.AddField("outdoor", playlist.fit?.outdoor ?? true);
                if ((playlist.fit?.minPlayers ?? 0) > 0) fit.AddField("minPlayers", playlist.fit.minPlayers);
                p.AddField("fit", fit);
                if (playlist.rules != null && !playlist.rules.IsDefault)
                {
                    var rules = new JSONObject(JSONObject.Type.Object);
                    rules.AddField("timerSec", playlist.rules.timerSec);
                    rules.AddField("scoring", playlist.rules.scoring ?? "");
                    rules.AddField("lives", playlist.rules.lives);
                    rules.AddField("advance", playlist.rules.advance ?? "on_complete");
                    rules.AddField("script", playlist.rules.script ?? "");
                    p.AddField("rules", rules);
                }
                p.AddField("totalMinutes", playlist.totalMinutes);
                var arc = new JSONObject(JSONObject.Type.Array);
                foreach (PlaylistLeg leg in playlist.arc)
                {
                    var l = new JSONObject(JSONObject.Type.Object);
                    l.AddField("id", leg.id);
                    l.AddField("name", leg.name);
                    l.AddField("beat", leg.beat);
                    l.AddField("minutes", leg.minutes);
                    l.AddField("next", leg.next ?? "walk_on");
                    l.AddField("titles", StringArray(leg.titles));
                    if (!string.IsNullOrEmpty(leg.objectiveText)) l.AddField("objectiveText", leg.objectiveText);
                    if (leg.bonusLevels != null && leg.bonusLevels.Count > 0) l.AddField("bonusLevels", StringArray(leg.bonusLevels));
                    if (leg.bonusBosses) l.AddField("bonusBosses", true);
                    if (leg.noBonus) l.AddField("noBonus", true);
                    var o = new JSONObject(JSONObject.Type.Object);
                    o.AddField("type", leg.objective.type);
                    PlaylistStop target = string.IsNullOrEmpty(leg.objective.targetAddress) ? null
                        : leg.stops.FirstOrDefault(s => s.resourceAddress == leg.objective.targetAddress);
                    if (target != null) o.AddField("target", target.attractionId);
                    if (leg.objective.count > 0) o.AddField("count", leg.objective.count);
                    if (leg.objective.seconds > 0) o.AddField("seconds", leg.objective.seconds);
                    l.AddField("objective", o);
                    var stops = new JSONObject(JSONObject.Type.Array);
                    foreach (PlaylistStop stop in leg.stops)
                    {
                        var s = new JSONObject(JSONObject.Type.Object);
                        s.AddField("contentId", stop.contentId);
                        s.AddField("attractionId", stop.attractionId);
                        s.AddField("resourceName", stop.resourceName);
                        s.AddField("role", stop.role);
                        var alternates = new JSONObject(JSONObject.Type.Array);
                        for (int i = 0; i < stop.alternateResourceNames.Count; i++)
                        {
                            var a = new JSONObject(JSONObject.Type.Object);
                            a.AddField("contentId", stop.contentId);
                            a.AddField("attractionId", i < stop.alternateAttractionIds.Count
                                ? stop.alternateAttractionIds[i] : "");
                            a.AddField("resourceName", stop.alternateResourceNames[i]);
                            alternates.Add(a);
                        }
                        s.AddField("alternates", alternates);
                        s.AddField("prefer", stop.placement?.prefer ?? "any");
                        if (!string.IsNullOrEmpty(stop.objectiveText)) s.AddField("objectiveText", stop.objectiveText);
                        if (!string.IsNullOrEmpty(stop.displayName)) s.AddField("displayName", stop.displayName);
                        if (!string.IsNullOrEmpty(stop.challenge)) s.AddField("challenge", stop.challenge);
                        if (stop.bonus != null && stop.bonus.Count > 0) s.AddField("bonus", StringArray(stop.bonus));
                        if (!string.IsNullOrEmpty(stop.introHint)) s.AddField("introHint", stop.introHint);
                        // § 6.0a authored locks (absent = never locked).
                        if (!string.IsNullOrEmpty(stop.id)) s.AddField("id", stop.id);
                        if (stop.lockedUntil != null && stop.lockedUntil.Count > 0)
                        {
                            var lu = new JSONObject(JSONObject.Type.Array);
                            foreach (string r in stop.lockedUntil) lu.Add(r);
                            s.AddField("lockedUntil", lu);
                        }
                        if (!string.IsNullOrEmpty(stop.options))
                        {
                            var options = Parse(stop.options);
                            if (options != null && options.type == JSONObject.Type.Object && options.count > 0) s.AddField("options", options);
                        }
                        stops.Add(s);
                    }
                    l.AddField("stops", stops);
                    arc.Add(l);
                }
                p.AddField("arc", arc);
                list.Add(PublishedRecord(playlist, p, manifest.contentId));
            }
            block.AddField("playlists", list);
            // Loud, before Web drops a record: every published stop / alternate / pool entry must carry its wire identity.
            foreach (string problem in Problems(block))
                Report?.Invoke($"[Playlists] {manifest.contentId}: Web will DROP this playlist — {problem}. Fix the playlist (Content Uploader → Playlists) before uploading.");
            return block;
        }

        /// The record Web receives (ww-upload, Oct 23; pl-web's server Web 36bc219 needs a sets record's lua + pool entries with
        /// contentId / attractionId / resourceName, challenge, options, role, goal, displayName; arc may be []): the playlist object AS AUTHORED in .dreampark-playlists.json (the
        /// compiler's verbatim `sourceJson`: pool, lua, setsMode, requires, requiresRelease, timing, dreamie… every field, known or
        /// not), with the compiled fields merged in only where the author did not write them (arc / pool stop addresses,
        /// contentId / attractionId / resourceName, fit / rules defaults). `titleId` = THIS upload's content id (a Beta upload
        /// says WizardsWayBeta, so older servers match the release); the authored base id rides as `baseTitleId`.
        public static JSONObject PublishedRecord(PackagePlaylist playlist, JSONObject compiled, string uploadContentId)
        {
            if (playlist.pool != null && playlist.pool.Count > 0)
            {
                var pool = new JSONObject(JSONObject.Type.Array);
                foreach (PlaylistStop e in playlist.pool)
                {
                    if (e == null) continue;
                    var j = new JSONObject(JSONObject.Type.Object);
                    if (!string.IsNullOrEmpty(e.id)) j.AddField("id", e.id);
                    if (!string.IsNullOrEmpty(e.contentId)) j.AddField("contentId", e.contentId);
                    if (!string.IsNullOrEmpty(e.attractionId)) j.AddField("attractionId", e.attractionId);
                    if (!string.IsNullOrEmpty(e.resourceName)) j.AddField("resourceName", e.resourceName);
                    if (!string.IsNullOrEmpty(e.resourceAddress)) j.AddField("resourceAddress", e.resourceAddress);
                    if (!string.IsNullOrEmpty(e.attraction)) j.AddField("attraction", e.attraction);
                    if (!string.IsNullOrEmpty(e.role)) j.AddField("role", e.role);
                    if (!string.IsNullOrEmpty(e.challenge)) j.AddField("challenge", e.challenge);
                    if (!string.IsNullOrEmpty(e.displayName)) j.AddField("displayName", e.displayName);
                    var eo = Parse(e.options);
                    if (eo != null && eo.type == JSONObject.Type.Object) j.AddField("options", eo);
                    if (!string.IsNullOrEmpty(e.goal) && e.goal != "default") { var gl = new JSONObject(JSONObject.Type.Object); gl.AddField("lua", e.goal); j.AddField("goal", gl); }
                    pool.Add(j);
                }
                compiled.SetField("pool", pool);
            }
            JSONObject authored = null;
            authored = Parse(playlist.sourceJson);
            JSONObject rec;
            if (authored != null && authored.type == JSONObject.Type.Object)
            {
                // a sets record that authored no arc gets the compiled empty one: `arc: []` (pl-web's server contract)
                rec = MergeAuthored(authored, compiled);
            }
            else rec = compiled;
            string authoredTitle = rec.GetField("titleId")?.type == JSONObject.Type.String ? rec.GetField("titleId").stringValue : null;
            if (!string.IsNullOrEmpty(uploadContentId))
            {
                rec.SetField("titleId", uploadContentId);
                if (!string.IsNullOrEmpty(authoredTitle) && authoredTitle != uploadContentId) rec.SetField("baseTitleId", authoredTitle);
            }
            return rec;
        }

        /// `authored` with `compiled`'s fields added where the author wrote none (objects key by key; arrays of objects matched
        /// by `id`, else by position when the lengths agree). Every authored value is kept exactly.
        public static JSONObject MergeAuthored(JSONObject authored, JSONObject compiled)
        {
            if (authored == null) return compiled;
            if (compiled == null) return authored;
            if (authored.type == JSONObject.Type.Object && compiled.type == JSONObject.Type.Object)
            {
                for (int i = 0; i < (compiled.keys?.Count ?? 0); i++)
                {
                    string k = compiled.keys[i];
                    var a = authored.GetField(k);
                    if (a == null) authored.AddField(k, compiled.list[i]);
                    else MergeAuthored(a, compiled.list[i]);
                }
                return authored;
            }
            if (authored.type == JSONObject.Type.Array && compiled.type == JSONObject.Type.Array)
            {
                var al = authored.list ?? new List<JSONObject>(); var cl = compiled.list ?? new List<JSONObject>();
                // Authored REFERENCES the compiler resolved into wire objects (a stop's `alternates`: "A_X" → {contentId,
                // attractionId, resourceName}): the wire form wins — Web rejects a string there (SAL Beta v25 lost 4 playlists).
                // (none resolved: [] — alternates are optional, the playlist is not)
                if (al.Count > 0 && al.All(x => x != null && x.type == JSONObject.Type.String)
                    && cl.All(x => x != null && x.type == JSONObject.Type.Object))
                {
                    authored.list.Clear();
                    foreach (var c in cl) authored.list.Add(c);
                    return authored;
                }
                bool ids = al.Count > 0 && al.All(x => x?.type == JSONObject.Type.Object && x.GetField("id")?.type == JSONObject.Type.String)
                           && cl.All(x => x?.type == JSONObject.Type.Object && x.GetField("id")?.type == JSONObject.Type.String);
                if (ids)
                {
                    foreach (var a in al)
                    {
                        var c = cl.FirstOrDefault(x => x.GetField("id").stringValue == a.GetField("id").stringValue);
                        if (c != null) MergeAuthored(a, c);
                    }
                }
                else if (al.Count == cl.Count)
                    for (int i = 0; i < al.Count; i++)
                        if (al[i]?.type == JSONObject.Type.Object && cl[i]?.type == JSONObject.Type.Object) MergeAuthored(al[i], cl[i]);
            }
            return authored;   // scalars and mismatched shapes: as authored
        }

        /// What Web (normalizePlaylistsBlock) would drop for want of a wire identity: an arc stop / alternate / pool entry with
        /// no attractionId or resourceName, an alternate that is not an object. Empty = every record arrives.
        public static List<string> Problems(JSONObject block)
        {
            var l = new List<string>();
            bool Ident(JSONObject o) => o != null && o.type == JSONObject.Type.Object
                && (!string.IsNullOrEmpty(o.GetField("attractionId")?.stringValue) || !string.IsNullOrEmpty(o.GetField("resourceName")?.stringValue));
            foreach (var p in block?.GetField("playlists")?.list ?? new List<JSONObject>())
            {
                string id = p?.GetField("id")?.stringValue ?? "?";
                var arc = p?.GetField("arc")?.list ?? new List<JSONObject>();
                for (int li = 0; li < arc.Count; li++)
                {
                    var stops = arc[li]?.GetField("stops")?.list ?? new List<JSONObject>();
                    for (int si = 0; si < stops.Count; si++)
                    {
                        if (!Ident(stops[si])) l.Add($"{id} leg {li + 1} stop {si + 1}: no attractionId / resourceName");
                        var alts = stops[si]?.GetField("alternates")?.list ?? new List<JSONObject>();
                        for (int ai = 0; ai < alts.Count; ai++)
                            if (!Ident(alts[ai])) l.Add($"{id} leg {li + 1} stop {si + 1} alternate {ai + 1}: not a resolved attraction ({alts[ai]?.Print()})");
                    }
                }
                var pool = p?.GetField("pool")?.list ?? new List<JSONObject>();
                for (int i = 0; i < pool.Count; i++)
                    if (!Ident(pool[i])) l.Add($"{id} pool {i + 1} ({pool[i]?.GetField("id")?.stringValue}): no attractionId / resourceName");
            }
            return l;
        }

        /// Where a would-be-dropped record is reported (loud: the Console's error list). Tests replace it.
        public static Action<string> Report = m => Debug.LogError(m);

        /// JSON text → JSON, or null when blank / unparseable (Defective JSON cannot parse "[]": SAL's `requires: []`).
        static JSONObject Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim();
            if (t == "[]") return new JSONObject(JSONObject.Type.Array);
            if (t == "{}") return new JSONObject(JSONObject.Type.Object);
            try { return new JSONObject(t); } catch (Exception) { return null; }
        }

        static JSONObject StringArray(IEnumerable<string> values)
        {
            var array = new JSONObject(JSONObject.Type.Array);
            foreach (string value in values ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(value)) array.Add(value);
            return array;
        }
    }
}
#endif
