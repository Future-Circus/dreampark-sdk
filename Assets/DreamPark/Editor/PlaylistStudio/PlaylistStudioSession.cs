#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DreamPark.Badges;
using DreamPark.SpaceMap;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// Everything the Playlist Studio (window, uploader summary, headless commands) knows about ONE title: its
    /// attractions (library), badges, challenge catalog, docs and the working copy of its playlists file. Built
    /// from the AssetDatabase; the pure rules it calls live in PlaylistStudio/Core.
    /// </summary>
    public sealed class PlaylistStudioSession
    {
        public string titleId, titleName;
        public List<PlaylistLibraryItem> library = new List<PlaylistLibraryItem>();
        public List<PlaylistLibrary.BadgeInfo> badges = new List<PlaylistLibrary.BadgeInfo>();
        public ContentPlaylistStore.Data data;
        public string loadError, docs = "";
        public bool dirty, hasCatalog;
        public readonly List<PlaylistProposal> proposals = new List<PlaylistProposal>();
        public List<PlaylistKind> offeredKinds = new List<PlaylistKind>();
        public readonly List<string> messages = new List<string>();
        /// The title's default goal blocks (key "<attractionId>.<kind>" → Lua), from Assets/Content/{id}/.dreampark-goals.
        public SortedDictionary<string, string> goals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// Goal blocks edited in the Studio but not yet written to disk (Save writes them with the playlists).
        public readonly HashSet<string> dirtyGoals = new HashSet<string>(StringComparer.Ordinal);

        Func<string, IReadOnlyList<PlaylistAttractionRef>> _resolve;
        PlaylistFootprintBook _book;
        public Func<string, IReadOnlyList<PlaylistAttractionRef>> Resolve => _resolve ??= PlaylistLibrary.Resolver(library);
        public PlaylistFootprintBook Book => _book ??= PlaylistLibrary.Book(titleId, library);
        public ISet<string> BadgeIds => badges.Count == 0 ? null : new HashSet<string>(badges.Select(b => b.id));

        public static string ChallengesPath(string contentId) => $"Assets/Content/{contentId}/.dreampark-challenges.json";

        static string Full(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        // ── build ──

        public static PlaylistStudioSession FromProject(string contentId, string titleName = null)
        {
            var s = new PlaylistStudioSession { titleId = contentId, titleName = string.IsNullOrEmpty(titleName) ? PlaylistLibrary.Pretty(contentId) : titleName };
            s.ReloadLibrary();
            s.ReloadData();
            return s;
        }

        public void ReloadData()
        {
            loadError = null;
            try
            {
                data = ContentPlaylistStore.Exists(titleId) ? PlaylistStudioJson.ReadData(File.ReadAllText(ContentPlaylistStore.FullPath(titleId))) : null;
            }
            catch (Exception e) { loadError = e.Message; data = null; }
            data ??= new ContentPlaylistStore.Data();
            dirty = false;
            ReloadGoals();
        }

        public string GoalsDir => Full(PlaylistGoalStore.AssetDir(titleId));

        public void ReloadGoals()
        {
            try { goals = PlaylistGoalStore.Load(GoalsDir); } catch (Exception e) { messages.Add("Goal blocks not read: " + e.Message); }
            dirtyGoals.Clear();
        }

        /// Edit one goal block in the working copy (null deletes it); written on Save.
        public void SetGoal(string key, string lua)
        {
            if (!PlaylistGoalStore.ValidKey(key)) return;
            if (lua == null) goals.Remove(key); else goals[key] = lua;
            dirtyGoals.Add(key);
            dirty = true;
        }

        // ── backend (source of truth) ──

        /// Replace the working copy with the backend's (GET …/playlists + …/goals). Nothing is written until Save.
        public void TakeBackend(JSONObject lists)
        {
            var pulled = PlaylistStudioSync.FromBackend(lists, out var pulledGoals);
            data = pulled;
            foreach (string k in goals.Keys.Except(pulledGoals.Keys).ToList()) dirtyGoals.Add(k);   // removed upstream
            goals = pulledGoals;
            foreach (string k in goals.Keys) dirtyGoals.Add(k);
            dirty = true;
        }

        /// The body of PUT …/playlists for this working copy (every playlist + the block + every goal block).
        public JSONObject BackendBody() => PlaylistStudioSync.ToBackend(data, titleId, goals);

        public void ReloadLibrary()
        {
            _resolve = null; _book = null;
            library.Clear(); badges.Clear();
            string root = $"Assets/Content/{titleId}";
            if (string.IsNullOrEmpty(titleId) || !AssetDatabase.IsValidFolder(root)) return;

            foreach (BadgeStore.Entry e in BadgeStore.Load(titleId) ?? new List<BadgeStore.Entry>())
                if (!string.IsNullOrEmpty(e.badgeId))
                    badges.Add(new PlaylistLibrary.BadgeInfo { id = e.badgeId, name = e.name, description = e.description, earnAt = e.earnAt?.ToList() ?? new List<string>() });

            var catalog = File.Exists(Full(ChallengesPath(titleId))) ? PlaylistChallenges.ReadCatalog(File.ReadAllText(Full(ChallengesPath(titleId)))) : new Dictionary<string, PlaylistChallenges.Row>();
            hasCatalog = catalog.Count > 0;

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0
                    || DreamSequenceGenerator.IsSpecialLevelPath(titleId, path)) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AttractionTemplate attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (attraction == null || attraction is DreamSequenceTemplate) continue;
                string leaf = Path.GetFileNameWithoutExtension(path);
                string stem = path.Substring("Assets/".Length, path.Length - "Assets/".Length - ".prefab".Length);
                Vector2 authored = new Vector2(attraction.Size.x, attraction.Size.z);
                var smaller = new List<Vector2>();
                if (attraction.HasPackingBake)
                {
                    authored = attraction.PackingBake.AuthoredFootprintMeters;
                    smaller.Add(attraction.PackingBake.ShrinkFootprintMeters);
                }
                if (attraction.HasLayoutFrames)
                    foreach (PackageFrameEnvelope f in DreamParkPackageCompiler.FrameEnvelopesFor(attraction))
                    { smaller.Add(f.authoredFootprintMeters); smaller.Add(f.shrinkFootprintMeters); }
                var luaTexts = AssetDatabase.GetDependencies(path, true)
                    .Where(d => d.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase) && d.StartsWith(root + "/", StringComparison.Ordinal))
                    .Select(d => { try { return File.ReadAllText(Full(d)); } catch { return ""; } }).ToList();
                var item = new PlaylistLibraryItem
                {
                    guid = guid, leaf = leaf, assetPath = path, resourceName = stem, displayName = attraction.displayName,
                    size = attraction.size.ToString(), hasPackingBake = attraction.HasPackingBake, authored = authored,
                    previewPath = PreviewPathFor(titleId, leaf),
                };
                item.ladder = PlaylistLibrary.Ladder(authored, smaller);
                item.roleHint = PlaylistLibrary.RoleHint(leaf, item.displayName, luaTexts);
                item.objectives = PlaylistLibrary.InferObjectives(item.roleHint, luaTexts, out item.objectivesSource);
                item.luaSignals = PlaylistLibrary.LuaSignals(luaTexts);
                item.badges = badges.Where(b => b.earnAt.Contains(leaf)).Select(b => b.id).ToList();
                item.notes = string.Join("; ", luaTexts.Select(LuaSummary).Where(x => x.Length > 0));
                if (item.notes.Length > 600) item.notes = item.notes.Substring(0, 600);
                if (catalog.TryGetValue(leaf, out var row)) PlaylistChallenges.Apply(item, row);
                library.Add(item);
            }
            library.Sort((a, b) => string.CompareOrdinal(a.leaf, b.leaf));

            var parts = new List<string>();
            foreach (string md in Directory.Exists(Full(root)) ? Directory.GetFiles(Full(root), "README.md", SearchOption.AllDirectories) : Array.Empty<string>())
                if (!md.Contains("~" + Path.DirectorySeparatorChar)) parts.Add("## " + md.Substring(Full(root).Length + 1) + "\n" + File.ReadAllText(md));
            docs = string.Join("\n\n", parts);
            if (docs.Length > 40000) docs = docs.Substring(0, 40000);
        }

        static string PreviewPathFor(string contentId, string leaf)
        {
            foreach (string p in new[] { $"Assets/Content/{contentId}/Previews/{leaf}.png", $"Assets/Content/{contentId}/Previews/{leaf}_preview.png" })
                if (File.Exists(Full(p))) return p;
            return null;
        }

        static string LuaSummary(string lua)
        {
            var lines = (lua ?? "").Split('\n').Select(l => l.Trim()).TakeWhile(l => l.StartsWith("--") || l.Length == 0)
                .Where(l => l.StartsWith("--")).Select(l => l.TrimStart('-').Trim()).Where(l => l.Length > 0).Take(4);
            return string.Join(" ", lines);
        }

        public PlaylistLibraryItem Item(string attraction)
        {
            if (string.IsNullOrEmpty(attraction)) return null;
            var found = Resolve(attraction);
            return found.Count == 1 ? library.FirstOrDefault(i => i.guid == found[0].guid) : null;
        }

        // ── check ──

        public List<PlaylistKind> Kinds => PlaylistProposals.MergeKinds(data, offeredKinds);

        /// All issues of the working copy: the uploader's validator, then chapters & unlocks, then challenges (warnings).
        public List<PlaylistIssue> Validate()
        {
            var issues = ContentPlaylistValidator.Validate(data, titleId, Resolve);
            issues.AddRange(PlaylistGates.Validate(data, BadgeIds, titleId));
            foreach (PackagePlaylist p in data.playlists) issues.AddRange(PlaylistChallenges.Validate(p, Item));
            return issues;
        }

        public List<PlaylistFitRow> Fit(PackagePlaylist p) => PlaylistFitCheck.Run(p, titleId, Book);

        // ── save ──

        public void Save()
        {
            string path = ContentPlaylistStore.FullPath(titleId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = PlaylistStudioJson.Serialize(data, titleId);
            if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
            foreach (string key in dirtyGoals)
            {
                if (goals.TryGetValue(key, out string lua)) PlaylistGoalStore.Write(GoalsDir, key, lua);
                else PlaylistGoalStore.Delete(GoalsDir, key);
            }
            dirtyGoals.Clear();
            dirty = false;
        }

        /// Accept a proposal into the working copy (kinds it needs are declared). False when its gates fail.
        public bool Accept(PlaylistProposal proposal)
        {
            if (proposal == null || !proposal.CanSave) return false;
            data.kinds = PlaylistProposals.KindsToSave(data, offeredKinds, data.playlists.Append(proposal.playlist));
            data.playlists.Add(PlaylistStudioJson.Clone(proposal.playlist, titleId));
            proposal.state = "accepted";
            dirty = true;
            return true;
        }

        public void Recheck(PlaylistProposal proposal) => PlaylistProposals.Check(proposal, titleId, Kinds, Resolve, Book);

        // ── generator ──

        public JSONObject GeneratorContext()
            => PlaylistLibrary.GeneratorContext(titleId, titleName, "", data.kinds, library, badges, docs, data);

        public List<PlaylistProposal> TakeResponse(GeneratorResponse response, string source)
        {
            offeredKinds = PlaylistProposals.MergeKinds(null, offeredKinds.Concat(response.kinds));
            var ctx = new PlaylistProposals.Context
            {
                titleId = titleId, existing = new ContentPlaylistStore.Data
                {
                    kinds = data.kinds,
                    playlists = data.playlists.Concat(proposals.Where(p => p.state != "rejected").Select(p => p.playlist)).ToList(),
                },
                library = library, badgeIds = BadgeIds, source = source,
            };
            var made = PlaylistProposals.Process(response, ctx, out var dropped);
            foreach (string d in dropped) messages.Add("Skipped: " + d);
            foreach (string w in response.warnings) messages.Add("Web: " + w);
            proposals.InsertRange(0, made);
            return made;
        }

        // ── migration ──

        public PlaylistMigration.Input MigrationInput()
        {
            var input = new PlaylistMigration.Input { titleId = titleId, titleName = titleName };
            foreach (PlaylistLibraryItem i in library) input.byGuid[i.guid] = i;
            List<PlaylistMigration.Entry> Entries(ContentSequenceStore.Data d) => (d?.items ?? new List<ContentSequenceStore.Entry>())
                .Where(e => e != null).Select(e => new PlaylistMigration.Entry
                {
                    name = e.name, isGroup = e.IsWorld, hidden = e.hidden,
                    guids = e.IsWorld ? (e.attractionGuids ?? new List<string>()).ToList()
                        : new List<string> { e.attractionGuid }.Where(g => !string.IsNullOrEmpty(g)).ToList(),
                }).ToList();
            string adv = ContentSequenceStore.AssetPath(titleId), seq = $"Assets/Content/{titleId}/.dreampark-dream-sequence.json";
            if (File.Exists(Full(adv)))
            {
                var d = ContentSequenceStore.Load(titleId, false);
                if (!string.IsNullOrEmpty(d.startGuid) && !string.IsNullOrEmpty(d.endGuid))
                { input.adventure = Entries(d); input.adventureStartGuid = d.startGuid; input.adventureEndGuid = d.endGuid; }
            }
            if (File.Exists(Full(seq))) input.sequence = Entries(ContentSequenceStore.Load(titleId, true));
            if (File.Exists(Full(ArenaPackageStore.AssetPath(titleId))))
            {
                try
                {
                    var a = ArenaPackageStore.Load(titleId);
                    var excluded = new HashSet<string>(a.excludedGuids ?? new List<string>());
                    input.arena = (a.buckets ?? new List<ArenaPackageStore.Bucket>()).OrderBy(b => b.widthFeet * b.lengthFeet)
                        .SelectMany(b => b.guids ?? new List<string>()).Where(k => !excluded.Contains(k) && k.IndexOf(ArenaPackageStore.FrameKeySeparator) < 0).ToList();
                }
                catch (Exception e) { messages.Add("Arena organizer not read: " + e.Message); }
            }
            return input;
        }

        /// One click: every organizer → draft proposals (the organizers are never touched).
        public List<PlaylistProposal> ConvertOrganizers()
        {
            PlaylistMigration.Result result = PlaylistMigration.Convert(MigrationInput());
            if (result.playlists.Count == 0) { messages.Add("No Adventure, Sequence or Arena organizer with attractions was found."); return new List<PlaylistProposal>(); }
            var response = new GeneratorResponse { success = true, kinds = result.kinds, playlists = result.playlists.Select(p => PlaylistStudioJson.WritePlaylist(p, titleId)).ToList() };
            foreach (string n in result.notes) messages.Add("Migration: " + n);
            return TakeResponse(response, "migrate");
        }
    }
}
#endif
