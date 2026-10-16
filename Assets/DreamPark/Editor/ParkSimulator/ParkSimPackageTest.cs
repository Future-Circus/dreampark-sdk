#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Editor;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public enum ParkSimPackageKind { Sequence = 0, Adventure = 1, Arena = 2 }

    [Serializable]
    public sealed class ParkSimPackageRequest
    {
        public string contentId;
        public ParkSimPackageKind kind;
        public long createdUtcTicks;
    }

    /// <summary>
    /// A one-session package test request. EditorPrefs carries the selection
    /// through Unity's edit-to-play domain reload; the request is cleared when
    /// Play ends. The simulator still owns environment, player, calibration,
    /// physics release and teardown.
    /// </summary>
    public static class ParkSimPackageTest
    {
        private static string RequestKey => "DreamPark.ParkSim.PackageTest." + Application.dataPath;
        private static readonly Vector2 CourtCenterXZ = new Vector2(-105f, 4f);
        private static readonly Quaternion CourtYaw = Quaternion.LookRotation(
            new Vector3(0.35f, 0f, 0.94f), Vector3.up);
        private static readonly ParkSimArenaPreviewState ArenaPreview =
            new ParkSimArenaPreviewState();
        private static readonly Dictionary<string, string> ArenaPaths =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ArenaNames =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static GameObject arenaGround;
        private static double arenaLastTick;
        private static bool arenaTickSubscribed;

        public static bool IsArenaPreview
        {
            get
            {
                ParkSimPackageRequest request = Current();
                return request != null && request.kind == ParkSimPackageKind.Arena;
            }
        }

        public static bool ArenaAutoPlaying => ArenaPreview.Automatic;
        public static Vector2 ArenaFloorFeet => ArenaPreview.FloorFeet;
        public static bool CanStepArenaSmaller => ArenaPreview.CanStepSmaller;
        public static bool CanStepArenaLarger => ArenaPreview.CanStepLarger;
        public static bool CanAutoArena => ArenaPreview.CanCycle;
        public static string ArenaSizeLabel => FormatFeet(ArenaPreview.FloorFeet.x) + " × "
            + FormatFeet(ArenaPreview.FloorFeet.y) + " ft"
            + (ArenaPreview.BucketCount > 0
                ? " · size " + (ArenaPreview.BucketIndex + 1) + "/" + ArenaPreview.BucketCount
                : "");
        public static string ArenaCandidateLabel
        {
            get
            {
                string guid = ArenaPreview.CurrentGuid;
                if (string.IsNullOrEmpty(guid)) return "No Arena candidate at this size";
                string name = ArenaNames.TryGetValue(guid, out string value) ? value : guid;
                return "Candidate " + (ArenaPreview.CandidateIndex + 1) + "/"
                    + ArenaPreview.CandidateCount + " · " + name;
            }
        }

        public static void Launch(string contentId, ParkSimPackageKind kind)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            ResetArenaPreview();
            var request = new ParkSimPackageRequest
            {
                contentId = contentId,
                kind = kind,
                createdUtcTicks = DateTime.UtcNow.Ticks,
            };
            EditorPrefs.SetString(RequestKey, JsonUtility.ToJson(request));
            ParkSimSettings.Enabled = true;
            if (EditorApplication.isPlaying) ParkSimulator.RegenerateWhenIdle();
            else EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
        }

        public static ParkSimPackageRequest Current()
        {
            string json = EditorPrefs.GetString(RequestKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var request = JsonUtility.FromJson<ParkSimPackageRequest>(json);
                if (request == null || string.IsNullOrEmpty(request.contentId)
                    || request.createdUtcTicks <= 0
                    || DateTime.UtcNow - new DateTime(request.createdUtcTicks, DateTimeKind.Utc)
                        > TimeSpan.FromMinutes(30))
                {
                    Clear();
                    return null;
                }
                return request;
            }
            catch (Exception)
            {
                Clear();
                return null;
            }
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(RequestKey);
            ResetArenaPreview();
        }

        public static void PrepareArena(ParkSimPackageRequest request, List<string> notes)
        {
            if (request == null || request.kind != ParkSimPackageKind.Arena) return;
            var candidates = DiscoverArenaCandidates(request.contentId);
            ArenaPaths.Clear();
            ArenaNames.Clear();
            // Arena keys are candidate keys: the GUID (Base) or "{guid}:{frameId}".
            var arenaCandidates = new List<ArenaPackageStore.Candidate>();
            foreach (ArenaAsset item in candidates)
            {
                foreach (ArenaPackageStore.Candidate candidate in
                    ArenaPackageStore.CandidatesForPrefab(item.guid, item.prefab))
                {
                    arenaCandidates.Add(candidate);
                    ArenaPaths[candidate.guid] = item.path;
                    ArenaNames[candidate.guid] = ArenaCandidateName(item.prefab, candidate);
                }
            }
            ArenaPackageStore.Data arenaLayout = ArenaPackageStore.LoadAndReconcile(request.contentId,
                arenaCandidates);
            ArenaPreview.Configure(request.contentId, arenaLayout.buckets);
            if (ArenaPreview.BucketCount == 0)
                notes?.Add("Arena Test has no included, size-bearing Attractions or Props.");
            EnsureArenaTick();
        }

        public static List<SpawnPoint> PlaceArenaAtOrigin()
            => new List<SpawnPoint>
            {
                new SpawnPoint
                {
                    markerName = "Arena " + ArenaSizeLabel,
                    position = Vector3.zero,
                    rotation = Quaternion.identity,
                    grounded = true,
                },
            };

        public static void BindArenaGround(GameObject ground)
        {
            arenaGround = ground;
            arenaLastTick = EditorApplication.timeSinceStartup;
            if (arenaGround == null) return;
            ParkSimPark.ResizeArenaGround(arenaGround, ArenaFloorFeet);
            if (ParkSimSettings.DriveCamera) ParkSimCamera.FrameArena(ArenaFloorFeet);
            EnsureArenaTick();
        }

        public static void StepArenaSize(int direction)
        {
            if (!IsArenaPreview || !ArenaPreview.StepSize(direction)) return;
            ApplyArenaFloor();
            ParkSimulator.RegenerateWhenIdle();
        }

        public static void ToggleArenaAuto()
        {
            if (!IsArenaPreview) return;
            ArenaPreview.ToggleAutomatic();
            arenaLastTick = EditorApplication.timeSinceStartup;
            SceneView.RepaintAll();
        }

        public static void ConfigureArenaInstance(GameObject instance)
        {
            if (instance == null || !IsArenaPreview) return;
            Vector2 target = ArenaFloorFeet * 0.3048f;
            // The Arena occurrence pins one layout frame ("base" for GUID keys).
            ArenaPackageStore.TryParseCandidateKey(ArenaPreview.CurrentGuid, out _, out string frameId);
            if (ApplyArenaLayout(instance, target, frameId))
            {
                ApplyArenaRect(instance, target);
                return;
            }

            PropTemplate prop = instance.GetComponent<PropTemplate>();
            if (prop != null && prop.TryGetAuthoredFootprintMeters(out Vector2 footprint))
                instance.transform.localRotation = ShouldRotateArenaFootprint(footprint, target)
                    ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
        }

        /// <summary>
        /// Fit an attraction into an Arena rectangle using the occurrence's pinned
        /// layout frame: orient by that frame's shrink footprint, then ApplyLayout at
        /// target / the frame's authored size (clamped to its range by the template).
        /// An unknown or unbaked frame id falls back to Base, as the runtime does.
        /// Frame-less attractions reproduce the former ApplyPackingVariant(scale,
        /// false) result exactly. False when the instance has no baked attraction.
        /// </summary>
        internal static bool ApplyArenaLayout(GameObject instance, Vector2 targetMeters, string frameId)
        {
            AttractionTemplate attraction = instance != null ? instance.GetComponent<AttractionTemplate>() : null;
            if (attraction == null || !attraction.HasPackingBake) return false;
            if (!attraction.TryGetFrameEnvelope(frameId, out AttractionFrameEnvelope envelope)
                && !attraction.TryGetFrameEnvelope(AttractionTemplate.BaseFrameId, out envelope))
                return false;
            bool rotate = ShouldRotateArenaFootprint(envelope.shrinkMeters, targetMeters);
            instance.transform.localRotation = rotate
                ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            Vector2 localTarget = rotate ? new Vector2(targetMeters.y, targetMeters.x) : targetMeters;
            Vector2 authored = envelope.authoredMeters;
            attraction.ApplyLayout(envelope.frameId, new Vector2(
                localTarget.x / authored.x, localTarget.y / authored.y));
            return true;
        }

        /// <summary>
        /// Seat the attraction on the simulated scan rect (DreamPark → Park Simulator →
        /// Arena Rect): walls and rect openings as core hands them over at runtime
        /// (wave 2, Oct 8 2026 — LayoutLab finding 9). "None" leaves the old behaviour.
        /// </summary>
        internal static void ApplyArenaRect(GameObject instance, Vector2 floorMeters)
        {
            AttractionTemplate attraction = instance != null ? instance.GetComponent<AttractionTemplate>() : null;
            if (attraction == null) return;
            ParkSimRect rect = ParkSimArenaRect.For(floorMeters);
            if (rect == null) return;
            float yaw = instance.transform.localEulerAngles.y;
            int turns = Mathf.RoundToInt(yaw / 90f);
            Vector3 p = instance.transform.localPosition;
            rect.Apply(attraction, new Vector2(p.x, p.z), turns);
            Debug.Log($"[ParkSim] {instance.name}: seated on simulated rect '{rect.id}' — walls {attraction.WallsWire}, "
                + attraction.RectOpenings);
        }

        private static string ArenaCandidateName(GameObject prefab, ArenaPackageStore.Candidate candidate)
        {
            if (candidate.IsBaseFrame) return prefab.name;
            AttractionTemplate attraction = prefab.GetComponent<AttractionTemplate>();
            string frameName = attraction != null
                && attraction.TryGetFrameEnvelope(candidate.frameId, out AttractionFrameEnvelope envelope)
                && !string.IsNullOrEmpty(envelope.name) ? envelope.name : candidate.frameId;
            return prefab.name + " · " + frameName;
        }

        internal static bool ShouldRotateArenaFootprint(Vector2 content, Vector2 target)
        {
            float directOverflow = Mathf.Max(0f, content.x - target.x)
                + Mathf.Max(0f, content.y - target.y);
            float rotatedOverflow = Mathf.Max(0f, content.y - target.x)
                + Mathf.Max(0f, content.x - target.y);
            return rotatedOverflow + 0.0001f < directOverflow;
        }

        public static void SelectPackagePlayer(ParkSimPackageRequest request, ScanResult scan)
        {
            if (request == null || scan == null) return;
            var matching = scan.players.Where(entry => entry != null
                && string.Equals(entry.contentFolder, request.contentId, StringComparison.Ordinal))
                .ToList();
            if (matching.Count == 0)
            {
                if (DreamParkDevice.SimulatePhone)
                {
                    // A phone without a Phone Player is a spectator: no rig at all,
                    // not some other package's.
                    scan.players.Clear();
                    return;
                }
                scan.notes.Add($"No Player prefab belongs to {request.contentId}; using the simulator's available Player rig(s).");
                return;
            }
            scan.players.Clear();
            scan.players.AddRange(matching);
        }

        public static List<ContentEntry> BuildEntries(ParkSimPackageRequest request,
            ScanResult scan, List<string> notes)
        {
            var selected = new List<ContentEntry>();
            if (request == null) return selected;
            string root = $"Assets/Content/{request.contentId}";
            if (request.kind == ParkSimPackageKind.Sequence)
            {
                string path = DreamSequencePackageCompiler.DefinitionPath(request.contentId);
                DreamSequencePackageDefinition definition = AssetDatabase.LoadAssetAtPath<
                    DreamSequencePackageDefinition>(path);
                if (definition == null) notes.Add("Sequence package definition is missing: " + path);
                else selected.Add(new ContentEntry
                {
                    displayName = definition.sequenceName,
                    kind = ContentKind.Attraction,
                    sequenceDefinition = definition,
                    sequenceContentId = request.contentId,
                    assetPath = path,
                    contentFolder = request.contentId,
                });
                return selected;
            }

            if (request.kind == ParkSimPackageKind.Arena)
            {
                PrepareArena(request, notes);
                string key = ArenaPreview.CurrentGuid;
                if (string.IsNullOrEmpty(key)) return selected;
                string path = ArenaPaths.TryGetValue(key, out string value)
                    ? value : AssetDatabase.GUIDToAssetPath(ArenaPackageStore.PrefabGuidOf(key));
                Add(path, scan, selected, notes);
                notes.Add("Arena Test is cycling the authored priority for "
                    + ArenaSizeLabel + ". " + ArenaCandidateLabel + ".");
                return selected;
            }

            var all = new List<string>();
            var attractions = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponent<DreamSequenceTemplate>() != null
                    || DreamSequenceGenerator.IsSpecialLevelPath(request.contentId, path)) continue;
                if (prefab.GetComponent<AttractionTemplate>() != null)
                {
                    all.Add(guid);
                    attractions.Add(guid);
                }
                else if (prefab.GetComponent<PropTemplate>() != null)
                {
                    all.Add(guid);
                }
            }
            ContentSequenceStore.Data layout = ContentSequenceStore.LoadAndReconcile(
                request.contentId, all, attractions, false);
            if (string.IsNullOrEmpty(layout.startGuid) || string.IsNullOrEmpty(layout.endGuid))
            {
                notes.Add("Adventure Test needs both a Start Point and an End Point in the package organizer.");
                return selected;
            }
            foreach (string guid in ContentSequenceStore.Flatten(layout))
                Add(AssetDatabase.GUIDToAssetPath(guid), scan, selected, notes);
            return selected;
        }

        private sealed class ArenaAsset
        {
            public string guid;
            public string path;
            public GameObject prefab;
            public bool isProp;
        }

        private static List<ArenaAsset> DiscoverArenaCandidates(string contentId)
        {
            string root = $"Assets/Content/{contentId}";
            return AssetDatabase.FindAssets("t:Prefab", new[] { root })
                .Select(guid => new ArenaAsset
                {
                    guid = guid,
                    path = AssetDatabase.GUIDToAssetPath(guid),
                })
                .Where(item => !string.IsNullOrEmpty(item.path)
                    && item.path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0
                    && !DreamSequenceGenerator.IsSpecialLevelPath(contentId, item.path))
                .Select(item =>
                {
                    item.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.path);
                    item.isProp = item.prefab != null
                        && item.prefab.GetComponent<PropTemplate>() != null;
                    return item;
                })
                .Where(item => item.prefab != null
                    && item.prefab.GetComponent<DreamSequenceTemplate>() == null
                    && (item.prefab.GetComponent<AttractionTemplate>() != null || item.isProp)
                    && ArenaPackageStore.CandidateForPrefab(item.guid, item.prefab).IsValid)
                .OrderBy(item => item.isProp ? 1 : 0)
                .ThenBy(item => item.prefab.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void EnsureArenaTick()
        {
            if (arenaTickSubscribed) return;
            EditorApplication.update += TickArena;
            arenaTickSubscribed = true;
            arenaLastTick = EditorApplication.timeSinceStartup;
        }

        private static void TickArena()
        {
            double now = EditorApplication.timeSinceStartup;
            float delta = Mathf.Clamp((float)(now - arenaLastTick), 0f, 0.25f);
            arenaLastTick = now;
            if (!Application.isPlaying || !IsArenaPreview || ParkSimulator.Stopped
                || ParkSimulator.IsGenerating || arenaGround == null
                || !ArenaPreview.Automatic) return;

            Vector2 before = ArenaFloorFeet;
            bool contentChanged = ArenaPreview.Tick(delta);
            if ((ArenaFloorFeet - before).sqrMagnitude > 0.000001f)
                ApplyArenaFloor();
            if (contentChanged) ParkSimulator.RegenerateWhenIdle();
        }

        private static void ApplyArenaFloor()
        {
            if (arenaGround != null)
                ParkSimPark.ResizeArenaGround(arenaGround, ArenaFloorFeet);
            if (ParkSimSettings.DriveCamera) ParkSimCamera.FrameArena(ArenaFloorFeet);
            SceneView.RepaintAll();
        }

        private static string FormatFeet(float value)
            => Mathf.Approximately(value, Mathf.Round(value))
                ? Mathf.RoundToInt(value).ToString()
                : value.ToString("0.0");

        private static void ResetArenaPreview()
        {
            if (arenaTickSubscribed)
            {
                EditorApplication.update -= TickArena;
                arenaTickSubscribed = false;
            }
            arenaGround = null;
            ArenaPaths.Clear();
            ArenaNames.Clear();
            ArenaPreview.Reset();
        }

        public static bool ConfigureSequenceLoader(GameObject instance)
        {
            if (instance == null) return false;
            DreamSequenceTemplate sequence = instance.GetComponent<DreamSequenceTemplate>();
            DreamLevelLoader loader = instance.GetComponent<DreamLevelLoader>();
            if (sequence == null || loader == null) return false;
            loader.prefabProvider = index =>
            {
                string guid = sequence.levels != null && index >= 0 && index < sequence.levels.Count
                    ? sequence.levels[index].sourceGuid : null;
                string path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = string.IsNullOrEmpty(path)
                    ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                return Cysharp.Threading.Tasks.UniTask.FromResult(prefab);
            };
            return true;
        }

        private static void Add(string path, ScanResult scan, List<ContentEntry> selected,
            List<string> notes)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                notes.Add("Package Test could not find prefab: " + path);
                return;
            }
            ContentKind kind = prefab.GetComponent<LevelTemplate>() != null
                ? ContentKind.Attraction : prefab.GetComponent<PropTemplate>() != null
                    ? ContentKind.Prop : ContentKind.Player;
            if (kind == ContentKind.Player)
            {
                notes.Add("Package Test skipped non-placeable prefab: " + path);
                return;
            }
            ContentEntry scene = scan?.placeables.FirstOrDefault(e => e.assetPath == path
                && e.hasUnappliedOverrides && e.sceneTemplate != null);
            selected.Add(new ContentEntry
            {
                displayName = prefab.name,
                kind = kind,
                prefabAsset = prefab,
                sceneTemplate = scene?.sceneTemplate,
                hasUnappliedOverrides = scene?.hasUnappliedOverrides ?? false,
                assetPath = path,
                contentFolder = ContentFolders.FolderOfAsset(path),
                fromScene = scene != null,
                fromSample = ContentFolders.IsUnderSample(path),
            });
        }

        public static List<SpawnPoint> PlaceSequenceOnCourt(
            IList<ContentEntry> entries, IList<SpawnPoint> markers, List<string> notes)
        {
            var result = new List<SpawnPoint>();
            if (entries == null || entries.Count == 0) return result;
            // The atlas blacktop at UV ~(.056,.073) maps to this world-space
            // court. Its 10 × 10 m patch varies only ~12 cm in the SDK scan.
            Vector3 probe = new Vector3(CourtCenterXZ.x, 100f, CourtCenterXZ.y);
            Vector3 ground = ParkSimPark.DropToGround(probe);
            bool hit = Mathf.Abs(ground.y - probe.y) > 0.01f;
            if (!hit)
            {
                SpawnPoint nearest = markers != null && markers.Count > 0
                    ? markers.OrderBy(p => (new Vector2(p.position.x, p.position.z) - CourtCenterXZ).sqrMagnitude).First()
                    : new SpawnPoint { markerName = "<no marker>", position = Vector3.zero, rotation = CourtYaw };
                ground = nearest.position;
                notes.Add("Basketball court ground was unavailable; Sequence used its nearest park marker.");
            }
            for (int i = 0; i < entries.Count; i++)
                result.Add(new SpawnPoint
                {
                    markerName = "Basketball court blacktop",
                    position = ground,
                    rotation = CourtYaw,
                    grounded = hit,
                });
            return result;
        }

        public static List<SpawnPoint> PlaceAdventurePath(
            IList<ContentEntry> entries, IList<SpawnPoint> markers, List<string> notes)
        {
            var result = new SpawnPoint[entries?.Count ?? 0];
            if (entries == null || entries.Count == 0) return new List<SpawnPoint>(result);
            var route = NearestNeighborRoute(markers);
            if (route.Count == 0)
            {
                notes.Add("No park markers were found; Adventure Test cannot build a spatial route.");
                return new List<SpawnPoint>(result);
            }
            var attractionIndices = Enumerable.Range(0, entries.Count)
                .Where(i => entries[i] != null && entries[i].kind == ContentKind.Attraction).ToList();
            float length = RouteLength(route);
            SpawnPoint previous = route[0];
            for (int stop = 0; stop < attractionIndices.Count; stop++)
            {
                float distance = attractionIndices.Count <= 1 ? 0f
                    : length * stop / (attractionIndices.Count - 1);
                SpawnPoint point = SampleRoute(route, distance);
                if (stop > 0)
                {
                    Vector3 towardPrevious = previous.position - point.position;
                    towardPrevious.y = 0f;
                    if (towardPrevious.sqrMagnitude > 0.001f)
                        point.rotation = Quaternion.LookRotation(towardPrevious.normalized, Vector3.up);
                }
                point.markerName = $"Adventure stop {stop + 1}/{attractionIndices.Count}";
                result[attractionIndices[stop]] = point;
                previous = point;
            }
            int propNumber = 0;
            SpawnPoint propHost = attractionIndices.Count > 0 ? result[attractionIndices[0]] : route[0];
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] == null || entries[i].kind != ContentKind.Prop)
                {
                    if (entries[i] != null && entries[i].kind == ContentKind.Attraction) propHost = result[i];
                    continue;
                }
                float angle = propNumber++ * 137.508f * Mathf.Deg2Rad;
                Vector3 candidate = propHost.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 5f;
                Vector3 probe = new Vector3(candidate.x, candidate.y + 40f, candidate.z);
                Vector3 dropped = ParkSimPark.DropToGround(probe);
                if ((dropped - probe).sqrMagnitude < 0.0001f) dropped = candidate;
                result[i] = new SpawnPoint
                {
                    markerName = propHost.markerName + " +prop",
                    position = dropped,
                    rotation = propHost.rotation,
                    grounded = true,
                };
            }
            notes.Add($"Adventure route follows {route.Count} park markers in package order; attractions face the previous stop.");
            return new List<SpawnPoint>(result);
        }

        private static List<SpawnPoint> NearestNeighborRoute(IList<SpawnPoint> markers)
        {
            var remaining = markers != null ? new List<SpawnPoint>(markers) : new List<SpawnPoint>();
            var route = new List<SpawnPoint>();
            if (remaining.Count == 0) return route;
            route.Add(remaining[0]);
            remaining.RemoveAt(0);
            while (remaining.Count > 0)
            {
                Vector3 current = route[route.Count - 1].position;
                int closest = 0;
                float best = float.PositiveInfinity;
                for (int i = 0; i < remaining.Count; i++)
                {
                    Vector3 delta = remaining[i].position - current;
                    float distance = delta.x * delta.x + delta.z * delta.z;
                    if (distance >= best) continue;
                    best = distance;
                    closest = i;
                }
                route.Add(remaining[closest]);
                remaining.RemoveAt(closest);
            }
            return route;
        }

        private static float RouteLength(IList<SpawnPoint> route)
        {
            float length = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                Vector3 a = route[i - 1].position, b = route[i].position;
                length += Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            }
            return length;
        }

        private static SpawnPoint SampleRoute(IList<SpawnPoint> route, float distance)
        {
            if (route.Count == 1) return route[0];
            for (int i = 1; i < route.Count; i++)
            {
                Vector3 a = route[i - 1].position, b = route[i].position;
                float segment = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                if (distance > segment && i < route.Count - 1)
                {
                    distance -= segment;
                    continue;
                }
                Vector3 point = Vector3.Lerp(a, b, segment > 0.001f ? Mathf.Clamp01(distance / segment) : 0f);
                Vector3 probe = new Vector3(point.x, point.y + 40f, point.z);
                Vector3 dropped = ParkSimPark.DropToGround(probe);
                bool hit = (dropped - probe).sqrMagnitude > 0.0001f;
                if (!hit) dropped = point;
                return new SpawnPoint
                {
                    markerName = route[i].markerName,
                    position = dropped,
                    rotation = route[i].rotation,
                    grounded = hit,
                };
            }
            return route[route.Count - 1];
        }
    }
}
#endif
