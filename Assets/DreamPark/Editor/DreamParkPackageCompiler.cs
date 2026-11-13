#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>Converts GUID-backed organizer data into a catalog-scoped runtime recipe.</summary>
    internal static class DreamParkPackageCompiler
    {
        internal static string ManifestPath(string contentId)
            => $"Assets/Content/{contentId}/{DreamParkPackageManifest.AssetName}.asset";

        /// <summary>
        /// The content folder an upload id reads from. A Beta upload builds under
        /// "{id}Beta" from the creator's one real folder "{id}" (BetaContentId), so
        /// path-based lookups keyed by the upload id must resolve that folder. A
        /// folder named after the id itself always wins; otherwise a Beta-shaped id
        /// maps to its release id when that folder exists. Anything else is returned
        /// unchanged (callers then find nothing, as before).
        /// </summary>
        internal static string SourceContentIdFor(string uploadContentId)
        {
            if (string.IsNullOrEmpty(uploadContentId)) return uploadContentId;
            if (AssetDatabase.IsValidFolder($"Assets/Content/{uploadContentId}")) return uploadContentId;
            string release = BetaContentId.ReleaseContentIdOf(uploadContentId);
            if (!string.IsNullOrEmpty(release) && AssetDatabase.IsValidFolder($"Assets/Content/{release}"))
                return release;
            return uploadContentId;
        }

        /// <summary>
        /// The package manifest asset path for an upload id (release or Beta).
        /// Oct 9 2026: DeliveryIndexGenerator and SmartBundleGrouper used
        /// ManifestPath(uploadId), which for "{id}Beta" named a folder that does not
        /// exist. Every Beta delivery index shipped `packages: {}` and the manifest
        /// never reached its tiny -Bootstrap bundle, so iOS fell back to downloading
        /// every -runtime bundle before PACK_PARK (CrashCourseBeta v5: 1.72 GB).
        /// </summary>
        internal static string ManifestPathForUpload(string uploadContentId)
            => ManifestPath(SourceContentIdFor(uploadContentId));

        internal static DreamParkPackageManifest Compile(string sourceContentId, string targetContentId = null)
        {
            if (string.IsNullOrEmpty(sourceContentId))
                throw new ArgumentException("A source content id is required.", nameof(sourceContentId));
            if (string.IsNullOrEmpty(targetContentId)) targetContentId = sourceContentId;
            string root = $"Assets/Content/{sourceContentId}";
            if (!AssetDatabase.IsValidFolder(root))
                throw new InvalidOperationException("Missing content folder: " + root);

            // Optional phone rig. Validated first so a broken slot fails the
            // compile with a clear message instead of shipping without it.
            string phonePlayerPath = PhonePlayerStore.ResolveSlotOrThrow(sourceContentId);
            string phonePlayerAddress = phonePlayerPath == null ? ""
                : targetContentId + "/" + Path.GetFileNameWithoutExtension(phonePlayerPath);
            string playerAddress = FindPlayerAddress(root, sourceContentId, targetContentId);
            string managerPath = DreamSequenceGenerator.ContainerPrefabPath(sourceContentId);
            GameObject manager = AssetDatabase.LoadAssetAtPath<GameObject>(managerPath);
            if (manager == null)
                managerPath = DreamSequenceGenerator.EnsureContainer(sourceContentId);
            string managerAddress = targetContentId + "/Game Container";
            var manifest = AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(ManifestPath(sourceContentId));
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<DreamParkPackageManifest>();
                AssetDatabase.CreateAsset(manifest, ManifestPath(sourceContentId));
            }

            manifest.schemaVersion = DreamParkPackageManifest.CurrentSchemaVersion;
            manifest.contentId = targetContentId;
            manifest.arena = CompileArenaRecipe(sourceContentId, targetContentId,
                playerAddress, managerAddress);
            manifest.adventure = CompileRecipe(sourceContentId, targetContentId, false,
                playerAddress, managerAddress);
            // Optional: an unfinished Sequence is left out, never shipped broken.
            manifest.sequence = DreamSequencePackageCompiler.IsAuthored(sourceContentId)
                ? CompileRecipe(sourceContentId, targetContentId, true, playerAddress, managerAddress)
                : null;
            if (manifest.sequence == null && DreamSequencePackageCompiler.IsStarted(sourceContentId))
                Debug.LogWarning("[DreamParkPackageCompiler] Sequence is unfinished (no attraction that fits the 12 × 18 ft room), so it is not included in this upload.");
            if (manifest.arena == null && manifest.adventure == null && manifest.sequence == null)
                Debug.LogWarning($"[DreamParkPackageCompiler] {sourceContentId} has no complete package recipe.");
            // One slot, shared by every package exactly like Player.
            foreach (PackageRecipe recipe in new[] { manifest.arena, manifest.adventure, manifest.sequence })
                if (recipe != null) recipe.phonePlayerAddress = phonePlayerAddress;
            if (manifest.sequence != null)
            {
                CopySequencePackingMetadata(sourceContentId, manifest.sequence);
                ValidateSequenceOrder(sourceContentId, manifest.sequence);
            }
            // Additive (Docs/PLAYLISTS.md): a title with no playlists file gets
            // playlistsSchemaVersion 0 and empty lists, nothing else changes.
            CompilePlaylists(sourceContentId, targetContentId, manifest,
                playerAddress, phonePlayerAddress, managerAddress);
            // Ways to play (ways-to-play contract §3): the director scripts + headers the headset runs (runtime handoff §1).
            manifest.runners = DreamPark.WaysToPlay.RunnerCatalog.BuildManifestRunners(root);
            // …and the levels only a runner loads (runner-only prefabs + the Sequence's Start / Game Over levels).
            manifest.runnerLevels = CompileRunnerLevels(sourceContentId, targetContentId);
            manifest.packageRevision = ComputePackageRevision(manifest);
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
            return manifest;
        }

        /// <summary>
        /// Ships the title's VALID playlists (ContentPlaylistValidator) in the
        /// manifest. A playlist with errors is left out with a warning, never
        /// shipped half-made, and never fails the rest of the upload.
        /// </summary>
        internal static void CompilePlaylists(string sourceContentId, string targetContentId,
            DreamParkPackageManifest manifest, string playerAddress, string phonePlayerAddress,
            string managerAddress)
        {
            manifest.playlistsSchemaVersion = 0;
            manifest.playlistsLayout = "";
            manifest.playlistGoalsJson = "";
            manifest.playlistKinds = new List<PlaylistKind>();
            manifest.playlists = new List<PackagePlaylist>();
            manifest.playlist = null;

            ContentPlaylistStore.Data data;
            try { data = ContentPlaylistStore.Load(sourceContentId); }
            catch (Exception e)
            {
                // An error, not a warning (json-escape, repair Oct 20 2026): an unreadable file ships the title with NO playlists.
                Debug.LogError("[DreamParkPackageCompiler] The playlists file could not be read, so this upload has NO playlists ("
                    + ContentPlaylistStore.AssetPath(sourceContentId) + "): " + e.Message);
                return;
            }
            if (data == null) return;
            Func<string, IReadOnlyList<PlaylistAttractionRef>> resolve =
                ContentPlaylistValidator.AssetResolver(sourceContentId);
            List<PlaylistIssue> issues = ContentPlaylistValidator.Validate(data, sourceContentId, resolve);
            foreach (PlaylistIssue issue in issues.Where(i => i.isError))
                Debug.LogWarning("[DreamParkPackageCompiler] Playlist " + issue);
            List<PackagePlaylist> valid = data.playlists
                .Where(p => ContentPlaylistValidator.IsValid(p.id, issues)).ToList();
            int skipped = data.playlists.Count - valid.Count;
            if (skipped > 0)
                Debug.LogWarning($"[DreamParkPackageCompiler] {skipped} playlist(s) have errors and are not included (Content Uploader → Playlists).");
            if (valid.Count == 0) return;
            if (string.IsNullOrEmpty(playerAddress))
            {
                Debug.LogWarning("[DreamParkPackageCompiler] Playlists need a PlayerRig prefab; not included.");
                return;
            }

            var recipe = new PackageRecipe
            {
                kind = "playlist",
                playerAddress = playerAddress,
                phonePlayerAddress = phonePlayerAddress,
                gameManagerAddress = managerAddress,
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            PlaylistAttractionRef Resolve(string reference) => resolve(reference)[0];
            string OccurrenceFor(PlaylistAttractionRef attraction)
            {
                string id = "playlist-" + attraction.guid;
                if (!ids.Contains(id))
                    AddOccurrence(recipe, ids, id, attraction.guid, "stage", null,
                        sourceContentId, targetContentId, false);
                return id;
            }

            // One stop's compiled addresses (an arc stop or a pool attraction): resourceAddress / resourceName / attractionId /
            // its recipe occurrence, the designed displayName, alternates.
            void FillStop(PlaylistStop stop)
            {
                if (PlaylistBookends.IsPlatformStop(stop.contentId, stop.attraction))
                {
                    // A platform bookend (PLAYLISTS.md § 11.2): built by the app, never in the
                    // title's recipe. Its address is also its resourceName.
                    string platformId = PlaylistBookends.IdOf(stop.attraction);
                    stop.attraction = platformId;
                    stop.contentId = PlaylistBookends.ContentId;
                    stop.resourceAddress = stop.resourceName = PlaylistBookends.AddressFor(platformId);
                    stop.attractionId = platformId;
                    stop.occurrenceId = "";
                    stop.alternates = new List<string>();
                    stop.alternateAttractionIds = new List<string>();
                    stop.alternateAddresses = new List<string>();
                    stop.alternateResourceNames = new List<string>();
                    stop.alternateOccurrenceIds = new List<string>();
                    return;
                }
                PlaylistAttractionRef main = Resolve(stop.attraction);
                stop.contentId = targetContentId;
                stop.resourceAddress = main.AddressFor(targetContentId);
                stop.resourceName = main.resourceName;
                stop.attractionId = DreamPark.API.GameStorageAPI.Slugify(main.resourceName);
                stop.alternateAttractionIds = new List<string>();
                stop.occurrenceId = OccurrenceFor(main);
                // The card / Dreamie name: the prefab's own AttractionTemplate.displayName (the designed
                // name, the same one the catalog push uploads) wins; the playlist's displayName is the fallback.
                string prefabName = PrefabDisplayName(main.guid);
                if (!string.IsNullOrEmpty(prefabName)) stop.displayName = prefabName;
                stop.alternateAddresses = new List<string>();
                stop.alternateResourceNames = new List<string>();
                stop.alternateOccurrenceIds = new List<string>();
                foreach (string alternate in stop.alternates)
                {
                    PlaylistAttractionRef alt = Resolve(alternate);
                    stop.alternateAddresses.Add(alt.AddressFor(targetContentId));
                    stop.alternateResourceNames.Add(alt.resourceName);
                    stop.alternateAttractionIds.Add(DreamPark.API.GameStorageAPI.Slugify(alt.resourceName));
                    stop.alternateOccurrenceIds.Add(OccurrenceFor(alt));
                }
            }

            foreach (PackagePlaylist playlist in valid)
            {
                playlist.totalMinutes = playlist.arc.Sum(leg => leg.minutes);
                // A title playlist belongs to the UPLOAD id (Beta included), like recipes.
                playlist.owner = "title";
                playlist.titleId = targetContentId;
                // pl-sets: a sets record's pool attractions compile exactly like arc stops (the device spawns a streamed /
                // added one by its address).
                foreach (PlaylistStop stop in playlist.pool ?? new List<PlaylistStop>()) FillStop(stop);
                foreach (PlaylistLeg leg in playlist.arc)
                {
                    leg.titles = new List<string> { targetContentId };
                    leg.mixed = false;
                    foreach (PlaylistStop stop in leg.stops) FillStop(stop);
                    PlaylistObjective objective = leg.objective;
                    if (string.IsNullOrEmpty(objective.target) && objective.type == "defeat_boss")
                    {
                        PlaylistStop boss = leg.stops.FirstOrDefault(s => s.role == "boss")
                            ?? leg.stops.FirstOrDefault(s => s.role == "miniboss");
                        objective.target = boss?.attraction;
                    }
                    objective.targetAddress = string.IsNullOrEmpty(objective.target) ? ""
                        : PlaylistBookends.IsKnown(objective.target) ? PlaylistBookends.AddressFor(objective.target)
                        : Resolve(objective.target).AddressFor(targetContentId);
                }
            }
            if (recipe.occurrences.Count > 2000)
            {
                Debug.LogWarning("[DreamParkPackageCompiler] Playlists exceed package limits; not included.");
                return;
            }
            manifest.playlistsSchemaVersion = PackagePlaylist.CurrentSchemaVersion;
            manifest.playlistKinds = ContentPlaylistStore.KindsOrDefault(data);
            manifest.playlists = valid;
            manifest.playlistCampaignsJson = data.campaignsJson ?? "";
            manifest.playlistFlagsJson = data.flagsJson ?? "";
            // repair-oct20 (CONTRACT § 2): the title's default goal blocks ship in the manifest (offline); the backend copy wins online.
            try
            {
                var raw = ContentPlaylistStore.SourceObjects(sourceContentId);
                foreach (PackagePlaylist p in valid) if (p != null && raw.TryGetValue(p.id ?? "", out string src)) p.sourceJson = src;
            }
            catch (Exception e) { Debug.LogWarning("[DreamParkPackageCompiler] playlist pass-through copy skipped: " + e.Message); }
            try { manifest.playlistGoalsJson = ContentPlaylistStore.GoalsJson(sourceContentId); }
            catch (Exception e) { manifest.playlistGoalsJson = ""; Debug.LogWarning("[DreamParkPackageCompiler] goal blocks not included: " + e.Message); }
            manifest.playlistsLayout = string.Equals(data.layout, "club", StringComparison.OrdinalIgnoreCase) ? "club" : "";
            manifest.playlist = recipe;
        }

        /// The prefab's AttractionTemplate.displayName (trimmed), or null.
        static string PrefabDisplayName(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var at = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
            string n = at != null ? (at.displayName ?? "").Trim() : "";
            return n.Length > 0 ? n : null;
        }

        internal static PackageRecipe CompileArenaRecipe(string sourceContentId,
            string targetContentId, string playerAddress, string managerAddress)
        {
            string root = $"Assets/Content/{sourceContentId}";
            var candidates = AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Select(guid => new { guid, path = AssetDatabase.GUIDToAssetPath(guid) })
                .Where(item => !string.IsNullOrEmpty(item.path)
                    && item.path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0
                    && !DreamPark.WaysToPlay.RunnerIds.IsRunnerOnlyAsset(item.path)
                    && !DreamSequenceGenerator.IsSpecialLevelPath(sourceContentId, item.path))
                .Select(item => new
                {
                    item.guid,
                    item.path,
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.path),
                })
                .Where(item => item.prefab != null
                    && item.prefab.GetComponent<DreamSequenceTemplate>() == null
                    && (item.prefab.GetComponent<AttractionTemplate>() != null
                        || item.prefab.GetComponent<PropTemplate>() != null))
                .OrderBy(item => item.prefab.GetComponent<PropTemplate>() != null ? 1 : 0)
                .ThenBy(item => item.prefab.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // One candidate per layout frame (Base keyed by GUID, frames by
            // "{guid}:{frameId}"); props and frame-less attractions are unchanged.
            ArenaPackageStore.Data layout = ArenaPackageStore.LoadAndReconcile(sourceContentId,
                candidates.SelectMany(item => ArenaPackageStore.CandidatesForPrefab(item.guid, item.prefab)));
            if (layout.buckets == null || layout.buckets.Count == 0) return null;
            if (string.IsNullOrEmpty(playerAddress))
                throw new InvalidOperationException("arena package has no PlayerRig prefab.");

            var recipe = new PackageRecipe
            {
                kind = "arena",
                playerAddress = playerAddress,
                gameManagerAddress = managerAddress,
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArenaPackageStore.Bucket sourceBucket in layout.buckets)
            {
                if (sourceBucket == null || sourceBucket.guids == null || sourceBucket.guids.Count == 0)
                    continue;
                var bucket = new ArenaSizeBucket
                {
                    widthFeet = sourceBucket.widthFeet,
                    lengthFeet = sourceBucket.lengthFeet,
                };
                foreach (string key in sourceBucket.guids)
                {
                    if (!ArenaPackageStore.TryParseCandidateKey(key, out string guid, out string frameId))
                        throw new InvalidOperationException("Arena layout has a malformed candidate key: " + key);
                    string occurrenceId = ArenaOccurrenceId(guid, frameId);
                    int before = recipe.occurrences.Count;
                    AddOccurrence(recipe, ids, occurrenceId, guid, "stage", null,
                        sourceContentId, targetContentId, false, true,
                        sourceBucket.widthFeet, sourceBucket.lengthFeet, frameId);
                    if (recipe.occurrences.Count > before)
                        bucket.occurrenceIds.Add(occurrenceId);
                }
                if (bucket.occurrenceIds.Count > 0) recipe.arenaBuckets.Add(bucket);
            }
            if (recipe.occurrences.Count == 0) return null;
            if (recipe.arenaBuckets.Count > 200 || recipe.occurrences.Count > 2000)
                throw new InvalidOperationException("arena package exceeds Web release limits.");
            return recipe;
        }

        /// <summary>
        /// Arena occurrence ids: "arena-{guid}" for the Base layout (unchanged from
        /// schema 3) and "arena-{guid}-{frameId}" for a pinned layout frame.
        /// </summary>
        internal static string ArenaOccurrenceId(string guid, string frameId)
            => ArenaPackageStore.IsBaseFrameId(frameId)
                ? "arena-" + guid
                : "arena-" + guid + "-" + frameId;

        internal static PackageRecipe CompileRecipe(string sourceContentId, string targetContentId,
            bool sequenceMode, string playerAddress, string managerAddress)
        {
            string layoutPath = sequenceMode
                ? $"Assets/Content/{sourceContentId}/.dreampark-dream-sequence.json"
                : ContentSequenceStore.AssetPath(sourceContentId);
            if (!File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", layoutPath))))
                return null;

            string root = $"Assets/Content/{sourceContentId}";
            var available = AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Where(g => !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g)))
                .Where(g => AssetDatabase.GUIDToAssetPath(g).IndexOf("/ThirdPartyLocal/",
                    StringComparison.OrdinalIgnoreCase) < 0)
                .Where(g => !DreamSequenceGenerator.IsSpecialLevelPath(sourceContentId,
                    AssetDatabase.GUIDToAssetPath(g)))
                .Where(g => !DreamPark.WaysToPlay.RunnerIds.IsRunnerOnlyAsset(AssetDatabase.GUIDToAssetPath(g)))
                .Where(g => IsPlacementPrefab(AssetDatabase.GUIDToAssetPath(g)))
                .ToList();
            var attractions = available.Where(g =>
                AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))
                    ?.GetComponent<AttractionTemplate>() != null).ToList();
            ValidateAuthoredReferences(ContentSequenceStore.Load(sourceContentId, sequenceMode),
                new HashSet<string>(available, StringComparer.Ordinal), sequenceMode);
            ContentSequenceStore.Data layout = ContentSequenceStore.LoadAndReconcile(sourceContentId,
                available, attractions, sequenceMode);
            if (!sequenceMode && (!layout.hasExplicitEndpoints
                || string.IsNullOrEmpty(layout.startGuid) || string.IsNullOrEmpty(layout.endGuid)))
                return null;

            var recipe = new PackageRecipe
            {
                kind = sequenceMode ? "sequence" : "adventure",
                playerAddress = playerAddress,
                gameManagerAddress = managerAddress,
                sequenceDefinitionAddress = sequenceMode
                    ? DreamSequencePackageDefinition.AddressFor(targetContentId) : null,
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!sequenceMode)
                AddOccurrence(recipe, ids, "start", layout.startGuid, "start", null,
                    sourceContentId, targetContentId, false);

            foreach (ContentSequenceStore.Entry item in layout.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null || item.hidden) continue;
                if (item.IsWorld)
                {
                    string groupId = RequiredId(item.id, "Group");
                    if (!ids.Add(groupId))
                        throw new InvalidOperationException("Duplicate package occurrence id: " + groupId);
                    recipe.groups.Add(new PackageGroupOccurrence
                    {
                        occurrenceId = groupId,
                        name = string.IsNullOrWhiteSpace(item.name) ? "Group" : item.name,
                        order = recipe.occurrences.Count,
                    });
                    for (int i = 0; i < (item.attractionGuids?.Count ?? 0); i++)
                    {
                        string childId = i < (item.attractionIds?.Count ?? 0)
                            ? item.attractionIds[i] : null;
                        AddOccurrence(recipe, ids, childId, item.attractionGuids[i], "stage", groupId,
                            sourceContentId, targetContentId, sequenceMode);
                    }
                }
                else
                    AddOccurrence(recipe, ids, item.id, item.attractionGuid, "stage", null,
                        sourceContentId, targetContentId, sequenceMode);
            }

            if (!sequenceMode)
                AddOccurrence(recipe, ids, "end", layout.endGuid, "end", null,
                    sourceContentId, targetContentId, false);

            if (recipe.occurrences.Count == 0) return null;
            if (sequenceMode && recipe.occurrences.All(o => o.role != "stage")) return null;
            if (recipe.groups.Count > 200 || recipe.occurrences.Count > 2000)
                throw new InvalidOperationException($"{recipe.kind} package exceeds Web release limits.");
            if (string.IsNullOrEmpty(recipe.playerAddress))
                throw new InvalidOperationException($"{recipe.kind} package has no PlayerRig prefab.");
            return recipe;
        }

        private static void ValidateAuthoredReferences(ContentSequenceStore.Data authored,
            HashSet<string> available, bool sequenceMode)
        {
            // Reconciliation is useful for the editor's reusable library, but
            // must not silently erase a deleted prefab from an authored
            // package. In particular that could make required content vanish
            // from a released recipe without any placement failure.
            if (authored == null || !authored.hasExplicitEndpoints) return;
            // An Adventure without BOTH endpoints is unfinished. CompileRecipe
            // leaves it out of the upload rather than shipping a broken route,
            // so nothing here can reach a recipe and must not block the upload.
            if (!sequenceMode && (string.IsNullOrEmpty(authored.startGuid)
                || string.IsNullOrEmpty(authored.endGuid)))
            {
                bool started = !string.IsNullOrEmpty(authored.startGuid)
                    || !string.IsNullOrEmpty(authored.endGuid)
                    || (authored.items != null && authored.items.Count > 0);
                if (started)
                    Debug.LogWarning("[DreamParkPackageCompiler] Adventure is unfinished (needs both a Start Point and an End Point), so it is not included in this upload.");
                return;
            }
            var referenced = new List<string>();
            if (!sequenceMode)
            {
                referenced.Add(authored.startGuid);
                referenced.Add(authored.endGuid);
            }
            foreach (ContentSequenceStore.Entry item in authored.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null || item.hidden) continue;
                if (item.IsWorld) referenced.AddRange(item.attractionGuids ?? new List<string>());
                else referenced.Add(item.attractionGuid);
            }
            foreach (string guid in referenced)
            {
                if (string.IsNullOrEmpty(guid) || !available.Contains(guid))
                    throw new InvalidOperationException($"{(sequenceMode ? "Sequence" : "Adventure")} package references a missing or invalid prefab ({(string.IsNullOrEmpty(guid) ? "no GUID" : guid)}). Remove or replace the placement before upload.");
            }
        }

        private static void AddOccurrence(PackageRecipe recipe, HashSet<string> ids,
            string occurrenceId, string guid, string role, string groupId,
            string sourceContentId, string targetContentId, bool sequenceMode,
            bool arenaMode = false, int widthFeet = 0, int lengthFeet = 0,
            string arenaFrameId = null)
        {
            occurrenceId = RequiredId(occurrenceId, role);
            if (!ids.Add(occurrenceId))
                throw new InvalidOperationException("Duplicate package occurrence id: " + occurrenceId);
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                throw new InvalidOperationException($"Package occurrence '{occurrenceId}' has no prefab: {guid}");
            AttractionTemplate attraction = prefab.GetComponent<AttractionTemplate>();
            PropTemplate prop = prefab.GetComponent<PropTemplate>();
            if (attraction == null && prop == null)
                throw new InvalidOperationException($"Package occurrence '{occurrenceId}' is not an attraction or prop.");
            if (sequenceMode)
            {
                if (attraction == null || attraction is DreamSequenceTemplate
                    || !attraction.HasPackingBake || !DreamSequenceCompatibility.IsCompatible(attraction))
                {
                    if (attraction != null && attraction.gameRequiresAttraction)
                        throw new InvalidOperationException(
                            $"Required Sequence occurrence '{prefab.name}' cannot fit its 12 × 18 ft room at its baked shrink size.");
                    // The editor shows incompatible assets dimmed to explain why
                    // they will not participate. Only included attractions are
                    // emitted, matching DreamSequencePackageCompiler's order.
                    Debug.LogWarning($"[DreamParkPackageCompiler] Skipping incompatible Sequence occurrence '{prefab.name}'.");
                    return;
                }
            }
            else if (!arenaMode && attraction != null && !attraction.HasPackingBake)
                throw new InvalidOperationException($"Adventure occurrence '{prefab.name}' has no packing bake.");
            // Arena candidates pin one layout frame; a frame key whose frame (or its
            // bake) is gone never reaches here after reconciliation, so this guards
            // hand-edited layouts rather than silently shipping the Base layout.
            string frameId = null;
            if (arenaMode)
            {
                frameId = ArenaPackageStore.IsBaseFrameId(arenaFrameId)
                    ? AttractionTemplate.BaseFrameId : arenaFrameId;
                if (frameId != AttractionTemplate.BaseFrameId
                    && (attraction == null || !attraction.TryGetFrameEnvelope(frameId, out _)))
                    throw new InvalidOperationException(
                        $"Arena occurrence '{occurrenceId}' pins layout frame '{frameId}', which '{prefab.name}' has no valid bake for.");
            }
            string resourceAddress = attraction != null
                ? $"{targetContentId}/Levels/{attraction.size}/{Path.GetFileNameWithoutExtension(path)}"
                : $"{targetContentId}/Props/{prop.category}/{Path.GetFileNameWithoutExtension(path)}";
            if (!path.StartsWith($"Assets/Content/{sourceContentId}/", StringComparison.Ordinal))
                throw new InvalidOperationException("Package occurrence is outside its content folder: " + path);
            var occurrence = new PackageOccurrence
            {
                occurrenceId = occurrenceId,
                resourceAddress = resourceAddress,
                role = prop != null ? "prop" : role,
                // Arena candidates are mutually exclusive alternatives. A candidate cannot be
                // required independently of the bucket selection, even when its source
                // Attraction marks itself as game-required for Adventure/Sequence.
                required = !arenaMode && (role == "start" || role == "end"
                    || (attraction != null && attraction.gameRequiresAttraction)),
                order = recipe.occurrences.Count,
                groupOccurrenceId = groupId,
                widthFeet = widthFeet,
                lengthFeet = lengthFeet,
                // "base" or a frame id for Arena; empty lets the runtime ladder choose.
                frameId = frameId,
            };
            CopyPackingMetadata(occurrence, attraction, prop);
            recipe.occurrences.Add(occurrence);
        }

        /// WAYS TO PLAY (Oct 26 2026): levels a runner may load that no package ships — every AttractionTemplate prefab under
        /// Content/&lt;Title&gt;/Runners/ (address RunnerIds.LevelAddress) plus the Sequence's Start Level / Game Over Level.
        /// ADDITIVE manifest list: today's app never reads it; RunnerLevelsFromManifest appends it after the packages.
        internal static List<PackageOccurrence> CompileRunnerLevels(string sourceContentId, string targetContentId)
        {
            var list = new List<PackageOccurrence>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, string address, string role)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var at = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (at == null || !seen.Add(address)) return;
                var o = new PackageOccurrence
                {
                    occurrenceId = "runner:" + Path.GetFileNameWithoutExtension(path),
                    resourceAddress = address,
                    role = role,
                    order = list.Count,
                };
                CopyPackingMetadata(o, at, null);
                list.Add(o);
            }
            string root = $"Assets/Content/{sourceContentId}";
            string runnersRoot = root + "/" + DreamPark.WaysToPlay.RunnerIds.FolderName;
            if (AssetDatabase.IsValidFolder(runnersRoot))
                foreach (string path in AssetDatabase.FindAssets("t:Prefab", new[] { runnersRoot })
                    .Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p)).OrderBy(p => p, StringComparer.Ordinal))
                    Add(path, DreamPark.WaysToPlay.RunnerIds.LevelAddress(targetContentId, Path.GetFileNameWithoutExtension(path)), "stage");
            foreach (var (path, role) in new[] { (DreamSequenceGenerator.StartLevelPath(sourceContentId), "start"),
                                                  (DreamSequenceGenerator.GameOverLevelPath(sourceContentId), "end") })
            {
                if (string.IsNullOrEmpty(path)) continue;
                var at = AssetDatabase.LoadAssetAtPath<GameObject>(path)?.GetComponent<AttractionTemplate>();
                if (at == null) continue;
                Add(path, $"{targetContentId}/Levels/{at.size}/{Path.GetFileNameWithoutExtension(path)}", role);
            }
            if (list.Count > 0) Debug.Log($"[DreamParkPackageCompiler] {list.Count} runner-only level(s): {string.Join(", ", list.Select(o => o.resourceAddress))}");
            return list;
        }

        private static void CopyPackingMetadata(PackageOccurrence occurrence,
            AttractionTemplate attraction, PropTemplate prop)
        {
            if (occurrence == null) return;
            if (attraction != null)
            {
                Vector2 authored = new Vector2(attraction.Size.x, attraction.Size.z);
                occurrence.authoredFootprintMeters = authored;
                occurrence.safeFootprintMeters = authored;
                occurrence.shrinkFootprintMeters = authored;
                occurrence.growFootprintMeters = authored;
                occurrence.essentialShrinkFootprintMeters = authored;
                occurrence.safeAreaInset = attraction.safeAreaInset;
                if (attraction.HasPackingBake)
                {
                    AttractionPackingBake bake = attraction.PackingBake;
                    occurrence.authoredFootprintMeters = bake.AuthoredFootprintMeters;
                    occurrence.safeFootprintMeters = bake.SafeFootprintMeters;
                    occurrence.shrinkFootprintMeters = bake.ShrinkFootprintMeters;
                    occurrence.growFootprintMeters = bake.GrowFootprintMeters;
                    occurrence.essentialShrinkFootprintMeters = bake.EssentialShrinkFootprintMeters;
                    occurrence.shrinkScale = bake.ShrinkScale;
                    occurrence.growScale = bake.GrowScale;
                    occurrence.essentialShrinkScale = bake.EssentialShrinkScale;
                    occurrence.hasEssentialProps = bake.HasEssentialProps;
                    occurrence.groundFootprintMeters = bake.GroundFootprintMeters;
                    occurrence.groundShrinkFootprintMeters = bake.GroundShrinkFootprintMeters;
                }
                occurrence.hasPackingMetadata = authored.x > 0f && authored.y > 0f;
                // Schema 4: the legacy fields above stay the Base envelope (with the
                // Minimum frame mirrored into essential*); the full ladder rides here.
                occurrence.frames = attraction.HasLayoutFrames
                    ? FrameEnvelopesFor(attraction)
                    : new List<PackageFrameEnvelope>();
                occurrence.portals = PortalsOf(attraction, AttractionTemplate.BaseFrameId);
                occurrence.hasPortals = true;
                return;
            }

            if (prop != null)
            {
                if (!prop.TryGetAuthoredFootprintMeters(out Vector2 footprint)) return;
                occurrence.authoredFootprintMeters = footprint;
                occurrence.safeFootprintMeters = footprint;
                occurrence.shrinkFootprintMeters = footprint;
                occurrence.growFootprintMeters = footprint;
                occurrence.essentialShrinkFootprintMeters = footprint;
                occurrence.hasPackingMetadata = footprint.x > 0f && footprint.y > 0f;
            }
        }

        /// <summary>The attraction's ladder (largest -> smallest authored area, Base included).</summary>
        internal static List<PackageFrameEnvelope> FrameEnvelopesFor(AttractionTemplate attraction)
        {
            var frames = new List<PackageFrameEnvelope>();
            if (attraction == null) return frames;
            foreach (AttractionFrameEnvelope envelope in attraction.GetFrameEnvelopes())
            {
                frames.Add(new PackageFrameEnvelope
                {
                    frameId = envelope.frameId,
                    kind = envelope.kind,
                    name = envelope.name,
                    authoredFootprintMeters = envelope.authoredMeters,
                    shrinkFootprintMeters = envelope.shrinkMeters,
                    growFootprintMeters = envelope.growMeters,
                    shrinkScale = envelope.shrinkScale,
                    growScale = envelope.growScale,
                    groundFootprintMeters = envelope.groundMeters,
                    groundShrinkFootprintMeters = envelope.groundShrinkMeters,
                    portals = PortalsOf(attraction, envelope.frameId),
                });
            }
            return frames;
        }

        /// <summary>A layout's effective entries / exits (AttractionTemplate.GetPortals) for the manifest.</summary>
        internal static List<PackagePortal> PortalsOf(AttractionTemplate attraction, string frameId)
        {
            var list = new List<PackagePortal>();
            if (attraction == null) return list;
            foreach (AttractionPortal p in attraction.GetPortals(frameId))
                if (p != null) list.Add(new PackagePortal { kind = (int)p.kind, position = AttractionPortalGeometry.Wrap(p.position) });
            return list;
        }

        private static void CopySequencePackingMetadata(string sourceContentId,
            PackageRecipe recipe)
        {
            DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                DreamSequencePackageDefinition>(DreamSequencePackageCompiler.DefinitionPath(sourceContentId));
            if (definition == null || recipe == null) return;

            Vector2 authored = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
                DreamSequenceTemplate.StandardLengthFeet) * 0.3048f;
            Vector2 minimum = definition.minimumRoomMeters;
            if (minimum.x <= 0f || minimum.y <= 0f) minimum = authored;
            Vector2 maximum = definition.maximumRoomMeters;
            if (maximum.x <= 0f || maximum.y <= 0f) maximum = authored;
            maximum = new Vector2(Mathf.Max(authored.x, maximum.x),
                Mathf.Max(authored.y, maximum.y));

            recipe.hasPackingMetadata = authored.x > 0f && authored.y > 0f;
            recipe.authoredFootprintMeters = authored;
            recipe.safeFootprintMeters = authored;
            recipe.shrinkFootprintMeters = minimum;
            recipe.growFootprintMeters = maximum;
            recipe.essentialShrinkFootprintMeters = minimum;
            recipe.shrinkScale = new Vector2(minimum.x / authored.x, minimum.y / authored.y);
            recipe.growScale = new Vector2(maximum.x / authored.x, maximum.y / authored.y);
            recipe.essentialShrinkScale = recipe.shrinkScale;
            recipe.safeAreaInset = 0f;
            recipe.hasEssentialProps = false;
        }

        private static string ComputePackageRevision(DreamParkPackageManifest manifest)
        {
            string previous = manifest.packageRevision;
            manifest.packageRevision = "";
            string json = JsonUtility.ToJson(manifest, false);
            manifest.packageRevision = previous;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                return string.Concat(digest.Select(value => value.ToString("x2")));
            }
        }

        private static string RequiredId(string id, string description)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(description + " has no stable occurrence id.");
            return id;
        }

        internal static bool IsValidAuthoredReference(string contentId, string guid)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(contentId)) return false;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string root = $"Assets/Content/{contentId}/";
            return !string.IsNullOrEmpty(path)
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0
                && !DreamSequenceGenerator.IsSpecialLevelPath(contentId, path)
                && IsPlacementPrefab(path);
        }

        private static bool IsPlacementPrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return prefab != null && (prefab.GetComponent<AttractionTemplate>() != null
                || prefab.GetComponent<PropTemplate>() != null);
        }

        // The headset Player. Never the Phone Player slot prefab, and never a
        // prefab named "Phone Player" (the template's default phone rig), even
        // when no prefab is named "Player": a second PlayerRig in the folder
        // must not be able to become the headset rig by sorting first.
        internal static string FindPlayerAddress(string root, string sourceContentId, string targetContentId)
        {
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Where(p => !PhonePlayerStore.IsPhonePlayerPrefab(sourceContentId, p))
                .OrderBy(p => string.Equals(Path.GetFileNameWithoutExtension(p), "Player",
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in paths)
            {
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<PlayerRig>() != null)
                    return targetContentId + "/" + Path.GetFileNameWithoutExtension(path);
            }
            return null;
        }

        private static void ValidateSequenceOrder(string sourceContentId, PackageRecipe recipe)
        {
            DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                DreamSequencePackageDefinition>(DreamSequencePackageCompiler.DefinitionPath(sourceContentId));
            if (definition == null)
                throw new InvalidOperationException("Sequence package definition has not been compiled.");
            List<string> manifestOrder = recipe.occurrences.Select(o => o.resourceAddress).ToList();
            List<string> definitionOrder = (definition.levels ?? new List<DreamSequenceLevel>())
                .Select(o => o.address).ToList();
            if (manifestOrder.Count != definitionOrder.Count
                || manifestOrder.Where((address, index) =>
                    !string.Equals(address.Substring(address.IndexOf("/Levels/", StringComparison.Ordinal)),
                        definitionOrder[index].Substring(definitionOrder[index].IndexOf("/Levels/", StringComparison.Ordinal)),
                        StringComparison.Ordinal)).Any())
                throw new InvalidOperationException("Sequence manifest and lazy-load definition have different level order.");
        }
    }
}
#endif
