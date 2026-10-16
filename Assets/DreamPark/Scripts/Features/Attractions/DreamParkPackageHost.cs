using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace DreamPark
{
    // Explicit values preserve every serialized Sequence/Adventure host while
    // allowing Arena to be the first option in authoring UI.
    public enum DreamParkPackageKind { Sequence = 0, Adventure = 1, Arena = 2 }

    /// <summary>
    /// Platform half of a package. This component loads and presents levels; it does
    /// not choose the next level, keep score, or implement game rules. Those belong
    /// to the creator's persistent Container prefab.
    /// </summary>
    public sealed class DreamParkPackageHost : MonoBehaviour
    {
        public DreamParkPackageKind kind = DreamParkPackageKind.Sequence;
        /// <summary>The title this package belongs to (manifest content id). Set by
        /// whoever creates the host. An Arena/Adventure Game Manager has no
        /// GameArea or LevelTemplate above it, so this is how its scripts know
        /// their title (dp.leader/rank, SharedState's title bus).</summary>
        [NonSerialized] public string contentId;
        public GameObject container;
        public Transform levelParent;
        public GameObject startLevel;
        public GameObject gameOverLevel;
        public GameObject overlayLevel;
        public GameObject transitionEffect;
        public DreamLevelLoader levelLoader;
        [Min(0f)] public float transitionDelaySeconds = 2.5f;
        public bool overlayAutoVisibility = true;
        [NonSerialized] public int adventureLevelCount;
        [NonSerialized] public List<string> adventureLevelNames;

        // Calibrated rooms are measured; accept a centimetre of slack when fitting.
        private const float RoomFitToleranceMeters = 0.01f;

        private int currentLevelIndex = 1;
        private int requestVersion;
        private bool changingLevel;
        private string lastLevelError;
        private Vector2 roomFootprintMeters;
        private readonly Dictionary<GameObject, Quaternion> authoredLevelRotations =
            new Dictionary<GameObject, Quaternion>();
        private GameObject gradedLevel;
        private MeshFilter gradedLevelFloor;
        private MeshFilter gradedReferenceFloor;
        private int gradedReferenceSignature = int.MinValue;
        private float nextGradeCheckAt;

        public int CurrentLevelIndex => currentLevelIndex;
        public bool IsChangingLevel => changingLevel;
        public string LastLevelError => lastLevelError;
        /// <summary>Raised when the requested slot has finished activating.</summary>
        public event Action<int> LevelActivated;
        /// <summary>
        /// Raised in the SAME frame the slot's level becomes visible (inside
        /// PresentLevel), before the frame is drawn. A Sequence change raises
        /// <see cref="LevelActivated"/> one drawn frame later (the new level's
        /// floor builds in its Start, under the transition), so a listener that
        /// must react before the new level is ever seen (hide an exit flash,
        /// teleport a prop) uses this. Lua: dp.on_level_presented(fn).
        /// Adventure/Arena raise it immediately before LevelActivated.
        /// </summary>
        public event Action<int> LevelPresented;
        /// <summary>Raised when a requested slot cannot be activated.</summary>
        public event Action<int> LevelChangeFailed;
        /// <summary>
        /// Oct 11 2026 (occurrence teardown): one attraction (occurrence) of this
        /// package is being removed while the package stays — an Arena rect set
        /// to "No Attraction", a swap, a repack retiring a rect. Raised BEFORE
        /// the occurrence's anchor is deactivated or destroyed, with
        /// (occurrenceId, resource address, anchor). Lua:
        /// dp.on_occurrence_removed(fn) — fn({occurrence_id, resource, anchor}).
        /// Content must still clean up in its own ondisable/ondestroy.
        /// </summary>
        public event Action<string, string, GameObject> OccurrenceRemoved;

        /// <summary>
        /// Platform call: announce that <paramref name="anchor"/>'s occurrence is
        /// going (see <see cref="OccurrenceRemoved"/>). Every C# listener and Lua
        /// handler runs isolated — one throwing never stops the others or the
        /// removal.
        /// </summary>
        public void RaiseOccurrenceRemoved(string occurrenceId, string resource, GameObject anchor)
        {
            Action<string, string, GameObject> listeners = OccurrenceRemoved;
            if (listeners != null)
                foreach (Delegate d in listeners.GetInvocationList())
                {
                    try { ((Action<string, string, GameObject>)d)(occurrenceId, resource, anchor); }
                    catch (Exception e) { Debug.LogWarning("[DreamParkPackageHost] OccurrenceRemoved listener threw: " + e.Message); }
                }
            DreamParkLuaAPI.DispatchOccurrenceRemoved(gameObject, occurrenceId, resource, anchor);
        }
        public int LevelCount => kind == DreamParkPackageKind.Sequence
            ? (levelLoader != null ? levelLoader.LevelCount : 0) + 2
            : adventureLevelCount > 0 ? adventureLevelCount
                : levelParent != null ? levelParent.childCount : 0;

        /// <summary>Number of playable stages in an authored Sequence Group.</summary>
        public int GroupLevelCount(string groupId)
        {
            groupId = ResolveGroupId(groupId);
            if (groupId == null) return 0;
            var levels = GetComponent<DreamSequenceTemplate>()?.levels;
            if (levels == null) return 0;
            int count = 0;
            foreach (DreamSequenceLevel level in levels)
                if (level != null && string.Equals(level.groupOccurrenceId, groupId,
                    StringComparison.Ordinal)) count++;
            return count;
        }

        /// <summary>Map a zero-based Group index to the package's one-based slot.</summary>
        public int ResolveGroupSlot(int groupIndex, string groupId)
        {
            if (groupIndex < 0) return 0;
            groupId = ResolveGroupId(groupId);
            if (groupId == null) return 0;
            var levels = GetComponent<DreamSequenceTemplate>()?.levels;
            if (levels == null) return 0;
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] == null || !string.Equals(levels[i].groupOccurrenceId,
                    groupId, StringComparison.Ordinal)) continue;
                if (groupIndex-- == 0) return i + 2;
            }
            return 0;
        }

        /// <summary>Package Group ID, or an unambiguous library Group ID alias.</summary>
        public string ResolveGroupId(string groupId)
        {
            if (kind != DreamParkPackageKind.Sequence || string.IsNullOrEmpty(groupId)) return null;
            var levels = GetComponent<DreamSequenceTemplate>()?.levels;
            if (levels == null) return null;
            foreach (DreamSequenceLevel level in levels)
                if (level != null && string.Equals(level.groupOccurrenceId, groupId,
                    StringComparison.Ordinal)) return groupId;

            string match = null;
            foreach (DreamSequenceLevel level in levels)
            {
                if (level == null || !string.Equals(level.sourceGroupId, groupId,
                    StringComparison.Ordinal) || string.IsNullOrEmpty(level.groupOccurrenceId)) continue;
                if (match != null && !string.Equals(match, level.groupOccurrenceId,
                    StringComparison.Ordinal)) return null;
                match = level.groupOccurrenceId;
            }
            return match;
        }

        private void Awake()
        {
            if (levelLoader == null) levelLoader = GetComponent<DreamLevelLoader>();
            if (levelLoader != null && levelParent != null) levelLoader.levelParent = levelParent;
            if (kind == DreamParkPackageKind.Sequence)
            {
                if (startLevel != null) startLevel.SetActive(true);
                if (gameOverLevel != null) gameOverLevel.SetActive(false);
                if (overlayLevel != null && overlayAutoVisibility) overlayLevel.SetActive(false);
            }
        }

        private void Start()
        {
            RefreshCalibratedRoom();
            // Oct 3 2026 (Sequence audit): warm the FIRST stage while the
            // guest stands in the Start Level. PresentLevel only preloaded
            // from stage 1 onward and the default Game Manager never calls
            // preload_level, so on a streaming mount (iOS catalog-only) the
            // Start button paid the whole stage-1 download behind the
            // transition particles.
            PreloadFirstStage();
        }

        private void PreloadFirstStage()
        {
            if (!Application.isPlaying || kind != DreamParkPackageKind.Sequence
                || levelLoader == null || !levelLoader.preloadNextOnBegin
                || currentLevelIndex != 1 || LevelCount <= 2) return;
            PreloadLevel(2);
        }

        private void Update()
        {
            RefreshCalibratedRoom();
            SynchronizeSequenceFloor();
            BindActiveAnchorsToPackageFloor();
        }

        private void RefreshCalibratedRoom()
        {
            // The park's placement/calibration path applies a packing variant to
            // the package root. Observe its actual footprint after that path has
            // run; no global room lookup or duplicate AR calibration is needed.
            AttractionTemplate owner = GetComponent<AttractionTemplate>();
            if (owner == null) return;
            Vector2 footprint = owner.RuntimeFootprintMeters;
            if (footprint.x <= 0f || footprint.y <= 0f) return;
            if ((footprint - roomFootprintMeters).sqrMagnitude > 0.000001f)
                SetRoomFootprintMeters(footprint);
        }

        /// <summary>
        /// 1-based package slot. Sequence slot 1 is Start, followed by streamed
        /// attractions, then Game Over. Returns false only for an invalid request;
        /// an Addressable failure is reported by DreamLevelLoader.LevelLoadFailed.
        /// </summary>
        public bool LoadLevel(int slot)
        {
            if (slot < 1 || slot > LevelCount || !isActiveAndEnabled)
            {
                lastLevelError = "invalid-or-inactive-slot";
                return false;
            }
            lastLevelError = null;
            if (kind != DreamParkPackageKind.Sequence)
            {
                // An Adventure is spatial: every attraction remains deployed.
                // The Container may use this as its current progression stop,
                // but changing it must never hide the rest of the park.
                currentLevelIndex = slot;
                RaiseLevelPresented(slot);
                LevelActivated?.Invoke(slot);
                return true;
            }
            if (slot == currentLevelIndex && !changingLevel)
            {
                RaiseLevelPresented(slot);
                LevelActivated?.Invoke(slot);
                return true;
            }
            int version = ++requestVersion;
            StartCoroutine(ChangeLevel(slot, version));
            return true;
        }

        /// <summary>Resolve a stage's prefab before it is requested for display.</summary>
        public bool PreloadLevel(int slot)
        {
            if (kind != DreamParkPackageKind.Sequence || levelLoader == null
                || slot < 2 || slot >= LevelCount) return false;
            levelLoader.LoadPrefabAsync(slot - 2).Forget();
            return true;
        }

        /// <summary>Whether the stage prefab is resident after a preload or load.</summary>
        public bool IsLevelReady(int slot)
        {
            return kind == DreamParkPackageKind.Sequence && levelLoader != null
                && slot >= 2 && slot < LevelCount
                && levelLoader.IsPrefabResident(slot - 2);
        }

        public void ShowOverlay(bool visible)
        {
            if (overlayLevel != null) overlayLevel.SetActive(visible);
        }

        public void SetOverlayAutoVisibility(bool enabled)
        {
            overlayAutoVisibility = enabled;
            if (enabled) UpdateOverlayVisibility(currentLevelIndex);
        }

        /// <summary>
        /// Called by the spatial placement owner when a calibrated room is selected.
        /// Baked layouts are applied to each active level, never to the source asset.
        /// </summary>
        public void SetRoomFootprintMeters(Vector2 footprint)
        {
            if (footprint.x <= 0f || footprint.y <= 0f) return;
            roomFootprintMeters = footprint;
            AttractionTemplate owner = GetComponent<AttractionTemplate>();
            if (owner != null && owner.HasPackingBake)
            {
                owner.ApplyPackingVariant(ScaleFor(owner, footprint), false);
                // Rect packing can request a size outside the Sequence bake.
                // All playable floors must use the footprint actually selected.
                roomFootprintMeters = owner.RuntimeFootprintMeters;
            }
            ApplyRoomFit(GetActiveLevel());
            ApplyRoomFit(overlayLevel);
            SynchronizeSequenceFloor();
            BindActiveAnchorsToPackageFloor();
        }

        private IEnumerator ChangeLevel(int slot, int version)
        {
            changingLevel = true;
            float earliestSwitch = Time.realtimeSinceStartup + Mathf.Max(0f, transitionDelaySeconds);
            PlayTransition();

            int loaderIndex = slot - 2;
            if (kind == DreamParkPackageKind.Sequence && slot > 1 && slot < LevelCount)
            {
                if (levelLoader == null) { FailChange(version, slot, "missing-loader"); yield break; }
                UniTask<GameObject> pending = levelLoader.LoadPrefabAsync(loaderIndex);
                var awaiter = pending.GetAwaiter();
                while (!awaiter.IsCompleted)
                {
                    if (version != requestVersion) yield break;
                    yield return null;
                }
                GameObject prefab;
                try { prefab = awaiter.GetResult(); }
                catch (Exception e) { Debug.LogException(e, this); FailChange(version, slot, "load-exception"); yield break; }
                if (prefab == null) { FailChange(version, slot, "download-or-load-failed"); yield break; }
                if (!CanFit(prefab.GetComponent<AttractionTemplate>(), roomFootprintMeters))
                {
                    Debug.LogError($"[DreamParkPackage] {prefab.name} cannot fit this calibrated room; level remains unchanged.", this);
                    FailChange(version, slot, "room-fit-failed");
                    yield break;
                }
            }

            while (Time.realtimeSinceStartup < earliestSwitch)
            {
                if (version != requestVersion) yield break;
                yield return null;
            }
            if (version != requestVersion) yield break;

            GameObject next = null;
            if (kind == DreamParkPackageKind.Sequence)
            {
                if (slot == 1) next = startLevel;
                else if (slot == LevelCount) next = gameOverLevel;
                else
                {
                    UniTask<GameObject> pending = levelLoader.SpawnLevelAsync(loaderIndex, false);
                    var awaiter = pending.GetAwaiter();
                    while (!awaiter.IsCompleted)
                    {
                        if (version != requestVersion) yield break;
                        yield return null;
                    }
                    try { next = awaiter.GetResult(); }
                    catch (Exception e) { Debug.LogException(e, this); FailChange(version, slot, "spawn-exception"); yield break; }
                }
            }
            else if (levelParent != null && slot <= levelParent.childCount)
                next = levelParent.GetChild(slot - 1).gameObject;

            if (next == null) { FailChange(version, slot, "spawn-failed"); yield break; }
            if (version != requestVersion) yield break;
            if (!PresentLevel(slot, next)) { FailChange(version, slot, "presentation-failed"); yield break; }
            // The new LevelTemplate builds its cutout floor in Start. Keep the
            // transition covering that frame, then transfer the reference grade
            // before revealing the level without particles.
            yield return null;
            if (version != requestVersion) yield break;
            SynchronizeSequenceFloor();
            changingLevel = false;
            StopTransition();
            LevelActivated?.Invoke(slot);
        }

        /// <summary>
        /// Present an already-loaded slot without a transition. Useful for a hub
        /// that preloads its destination, editor simulation, and deterministic
        /// integration tests. Does not choose progression or fetch assets.
        /// </summary>
        public bool ActivatePreparedLevel(int slot)
        {
            if (slot < 1 || slot > LevelCount) return false;
            if (kind != DreamParkPackageKind.Sequence)
            {
                currentLevelIndex = slot;
                return true;
            }
            GameObject next = null;
            if (kind == DreamParkPackageKind.Sequence)
                next = slot == 1 ? startLevel : slot == LevelCount ? gameOverLevel
                    : levelLoader != null ? levelLoader.GetInstance(slot - 2) : null;
            else if (levelParent != null && slot <= levelParent.childCount)
                next = levelParent.GetChild(slot - 1).gameObject;
            bool presented = PresentLevel(slot, next);
            if (presented) LevelActivated?.Invoke(slot);
            return presented;
        }

        private bool PresentLevel(int slot, GameObject next)
        {
            if (next == null || !ApplyRoomFit(next)) return false;
            int oldSlot = currentLevelIndex;
            GameObject old = GetActiveLevel();
            if (old != null && old != next) old.SetActive(false);
            if (!next.activeSelf) next.SetActive(true);
            currentLevelIndex = slot;
            if (kind == DreamParkPackageKind.Sequence && levelLoader != null
                && oldSlot > 1 && oldSlot < LevelCount && oldSlot != slot)
            {
                levelLoader.DespawnLevel(oldSlot - 2);
                if (old != null) authoredLevelRotations.Remove(old);
            }
            UpdateOverlayVisibility(slot);
            SynchronizeSequenceFloor();
            BindActiveAnchorsToPackageFloor();
            RaiseLevelPresented(slot);
            if (Application.isPlaying && kind == DreamParkPackageKind.Sequence
                && levelLoader != null && levelLoader.preloadNextOnBegin
                && slot >= 1 && slot < LevelCount)
                // Slot 1 (Start, e.g. a restart) warms stage index 0.
                levelLoader.PreloadNext(slot - 2);
            return true;
        }

        private void RaiseLevelPresented(int slot)
        {
            var handlers = LevelPresented;
            if (handlers == null) return;
            // A listener that throws must not abort the presentation (the
            // transition would never stop and LevelActivated never fire).
            foreach (Action<int> h in handlers.GetInvocationList())
            {
                try { h(slot); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
        }

        private GameObject GetActiveLevel()
        {
            if (kind == DreamParkPackageKind.Sequence)
            {
                if (currentLevelIndex == 1) return startLevel;
                if (currentLevelIndex == LevelCount) return gameOverLevel;
                return levelLoader != null ? levelLoader.GetInstance(currentLevelIndex - 2) : null;
            }
            return levelParent != null && currentLevelIndex > 0 && currentLevelIndex <= levelParent.childCount
                ? levelParent.GetChild(currentLevelIndex - 1).gameObject : null;
        }

        private void UpdateOverlayVisibility(int slot)
        {
            if (!overlayAutoVisibility || overlayLevel == null) return;
            overlayLevel.SetActive(kind == DreamParkPackageKind.Sequence && slot > 1 && slot < LevelCount);
        }

        private bool ApplyRoomFit(GameObject level)
        {
            if (level == null) return true;
            AttractionTemplate template = level.GetComponent<AttractionTemplate>();
            if (template == null) return true;
            if (kind == DreamParkPackageKind.Sequence)
                template.generateFloor = level != overlayLevel;
            if (roomFootprintMeters.x <= 0f || roomFootprintMeters.y <= 0f) return true;
            if (!template.HasPackingBake)
            {
                Debug.LogWarning($"[DreamParkPackage] {level.name} has no valid packing bake; calibrated room fit cannot be applied.", level);
                return false;
            }
            // Bind before applying a baked variant: follower recaches must sample
            // this level's floor, never a nearby attraction or the calibration reference.
            BindAnchorsToPackageFloor(level);
            // target becomes the room in the level's own (possibly rotated) axes.
            Vector2 target = roomFootprintMeters;
            bool fitted = template.HasLayoutFrames
                ? ApplyFrameRoomFit(level, template, ref target)
                : ApplyLegacyRoomFit(level, template, ref target);
            if (!fitted) return false;
            if (kind == DreamParkPackageKind.Sequence && level != overlayLevel)
            {
                // The Sequence, not a nested attraction, owns the saved grade.
                // Its floor grid spans the packed room even when this level's
                // props reach only their smaller baked maximum.
                template.floorData = null;
                int density = GetComponent<LevelTemplate>()?.gridDensity ?? template.gridDensity;
                template.ConfigureSequenceFloor(target, Mathf.Max(1, density));
            }
            // Baked props have moved to new X/Z positions. Re-select floor samples
            // there, while preserving each anchor's authored vertical offset.
            foreach (FloorAnchor anchor in level.GetComponentsInChildren<FloorAnchor>(true))
                anchor.RecacheCorners();
            level.GetComponent<GameArea>()?.ComputeBounds();
            level.GetComponent<MusicArea>()?.ComputeBounds();
            return true;
        }

        // Streamed Sequence stages may turn 90° to use the room; Start, Overlay
        // and Game Over always keep their authored orientation.
        private bool IsStreamedStage(GameObject level) =>
            kind == DreamParkPackageKind.Sequence && level != startLevel && level != gameOverLevel
                && level != overlayLevel;

        private Quaternion AuthoredRotationOf(GameObject level)
        {
            if (!authoredLevelRotations.TryGetValue(level, out Quaternion authoredRotation))
            {
                authoredRotation = level.transform.localRotation;
                authoredLevelRotations[level] = authoredRotation;
            }
            return authoredRotation;
        }

        /// <summary>Frame-less levels: one baked layout, scaled within its own range (unchanged).</summary>
        private bool ApplyLegacyRoomFit(GameObject level, AttractionTemplate template, ref Vector2 target)
        {
            if (IsStreamedStage(level))
            {
                Quaternion authoredRotation = AuthoredRotationOf(level);
                Vector2 minimum = template.PackingBake.ShrinkFootprintMeters;
                bool directFits = minimum.x <= target.x + 0.01f && minimum.y <= target.y + 0.01f;
                bool rotatedFits = minimum.y <= target.x + 0.01f && minimum.x <= target.y + 0.01f;
                bool rotate = rotatedFits && (!directFits || DreamSequenceCompatibility.ShouldRotate(template));
                level.transform.localRotation = rotate
                    ? authoredRotation * Quaternion.Euler(0f, 90f, 0f) : authoredRotation;
                if (rotate)
                {
                    target = new Vector2(target.y, target.x);
                }
            }
            Vector2 scale = ScaleFor(template, target);
            Vector2 applied = template.GetPackingFootprintMeters(scale, false);
            if (!CanFit(template, target) || applied.x > target.x + 0.01f || applied.y > target.y + 0.01f)
            {
                Debug.LogError($"[DreamParkPackage] {level.name} cannot fit calibrated room {target}; minimum is {template.PackingBake.ShrinkFootprintMeters}.", level);
                return false;
            }
            // ApplyPackingVariant clamps to the attraction's baked shrink/grow
            // limits. Empty space is intentional when its maximum is smaller than
            // the room; scaling the whole level would stretch every mesh and collider.
            template.ApplyPackingVariant(scale, false);
            return true;
        }

        /// <summary>
        /// Levels with layout frames: pick ONE hand-authored frame for this room
        /// (largest whose shrink fits; stages may rotate) and apply it within that
        /// frame's own shrink/grow range. Nothing is applied when no frame fits.
        /// </summary>
        private bool ApplyFrameRoomFit(GameObject level, AttractionTemplate template, ref Vector2 target)
        {
            bool stage = IsStreamedStage(level);
            Vector2 room = target;
            if (!DreamSequenceCompatibility.TryChooseFrame(template, room, stage, RoomFitToleranceMeters,
                    out string frameId, out Vector2 frameScale, out bool rotate))
            {
                Debug.LogError($"[DreamParkPackage] {level.name} cannot fit calibrated room {room}; no layout frame fits{(stage ? " either way round" : "")} (smallest shrink is {template.MinimumFootprintMeters}).", level);
                return false;
            }
            Vector2 oriented = rotate ? new Vector2(room.y, room.x) : room;
            Vector2 applied = template.GetFrameFootprintMeters(frameId, frameScale);
            if (applied.x > oriented.x + RoomFitToleranceMeters || applied.y > oriented.y + RoomFitToleranceMeters)
            {
                Debug.LogError($"[DreamParkPackage] {level.name} cannot fit calibrated room {room}; layout frame '{frameId}' needs {applied}.", level);
                return false;
            }
            if (stage)
            {
                Quaternion authoredRotation = AuthoredRotationOf(level);
                level.transform.localRotation = rotate
                    ? authoredRotation * Quaternion.Euler(0f, 90f, 0f) : authoredRotation;
            }
            if (!template.ApplyLayout(frameId, frameScale))
            {
                Debug.LogError($"[DreamParkPackage] {level.name} could not apply layout frame '{frameId}'.", level);
                return false;
            }
            target = oriented;
            Debug.Log($"[DreamParkPackage] {level.name} uses layout frame '{frameId}' at scale ({frameScale.x:0.###}, {frameScale.y:0.###}){(rotate ? " rotated 90°" : "")} for calibrated room {room}.", level);
            return true;
        }

        private void BindActiveAnchorsToPackageFloor()
        {
            if (kind != DreamParkPackageKind.Sequence) return;
            GameObject active = GetActiveLevel();
            BindAnchorsToPackageFloor(active);
            if (overlayLevel != null && overlayLevel.activeInHierarchy)
                BindAnchorsToLevelFloor(overlayLevel, active);
            if (transitionEffect != null)
                BindAnchorsToLevelFloor(transitionEffect, active);
        }

        private void BindAnchorsToPackageFloor(GameObject level)
        {
            BindAnchorsToLevelFloor(level, level == overlayLevel ? GetActiveLevel() : level);
        }

        private void BindAnchorsToLevelFloor(GameObject content, GameObject surfaceLevel)
        {
            if (kind != DreamParkPackageKind.Sequence || content == null || surfaceLevel == null) return;
            LevelTemplate owner = surfaceLevel.GetComponent<LevelTemplate>();
            if (owner == null || owner.runtimePlane == null) return;
            MeshFilter floor = owner.runtimePlane.GetComponent<MeshFilter>();
            CalibrateLevel calibration = owner.runtimePlane.GetComponent<CalibrateLevel>();
            if (floor == null || calibration == null) return;
            foreach (FloorAnchor anchor in content.GetComponentsInChildren<FloorAnchor>(true))
                anchor.BindToFloor(floor, calibration);
        }

        // Either orientation may be used (streamed stages). Levels with layout
        // frames fit when any baked frame's shrink fits.
        private static bool CanFit(AttractionTemplate template, Vector2 target)
        {
            if (target.x <= 0f || target.y <= 0f) return true;
            if (template == null || !template.HasPackingBake) return false;
            if (template.HasLayoutFrames)
                return DreamSequenceCompatibility.TryChooseFrame(template, target, true,
                    RoomFitToleranceMeters, out _, out _, out _);
            Vector2 minimum = template.PackingBake.ShrinkFootprintMeters;
            bool direct = minimum.x <= target.x + 0.01f && minimum.y <= target.y + 0.01f;
            bool rotated = minimum.y <= target.x + 0.01f && minimum.x <= target.y + 0.01f;
            return direct || rotated;
        }

        // Base-relative scale for the package owner and frame-less levels. Levels
        // with layout frames get a frame-relative scale from TryChooseFrame.
        private static Vector2 ScaleFor(AttractionTemplate template, Vector2 target)
        {
            Vector2 authored = template.PackingBake.AuthoredFootprintMeters;
            return new Vector2(target.x / authored.x, target.y / authored.y);
        }

        private void SynchronizeSequenceFloor()
        {
            if (kind != DreamParkPackageKind.Sequence) return;
            LevelTemplate referenceOwner = GetComponent<LevelTemplate>();
            GameObject active = GetActiveLevel();
            LevelTemplate levelOwner = active != null ? active.GetComponent<LevelTemplate>() : null;
            if (referenceOwner?.runtimePlane == null || levelOwner?.runtimePlane == null) return;

            GameObject reference = referenceOwner.runtimePlane;
            GameObject surface = levelOwner.runtimePlane;
            MeshFilter referenceFilter = reference.GetComponent<MeshFilter>();
            MeshFilter surfaceFilter = surface.GetComponent<MeshFilter>();
            CalibrateLevel source = reference.GetComponent<CalibrateLevel>();
            CalibrateLevel destination = surface.GetComponent<CalibrateLevel>();
            if (referenceFilter == null || surfaceFilter == null || source == null || destination == null) return;

            // Keep the reference mesh for one park-layout calibration payload,
            // but never leave its collider/NavMesh underneath a level's lava pit.
            MeshCollider referenceCollider = reference.GetComponent<MeshCollider>();
            if (referenceCollider != null) referenceCollider.enabled = false;
            MeshRenderer referenceRenderer = reference.GetComponent<MeshRenderer>();
            if (referenceRenderer != null) referenceRenderer.enabled = false;
            var referenceNav = reference.GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            if (referenceNav != null) referenceNav.enabled = false;

            if (!source.calibrated) return;
            if (gradedLevel == active && gradedLevelFloor == surfaceFilter
                && gradedReferenceFloor == referenceFilter && !source.isCalibrating
                && Time.unscaledTime < nextGradeCheckAt)
                return;
            nextGradeCheckAt = Time.unscaledTime + 0.25f;
            int signature = GradeSignature(referenceFilter);
            if (gradedLevel == active && gradedLevelFloor == surfaceFilter
                && gradedReferenceFloor == referenceFilter && gradedReferenceSignature == signature)
                return;
            if (!destination.TransferGradeFrom(source.CaptureGradeWorldSamples())) return;
            gradedLevel = active;
            gradedLevelFloor = surfaceFilter;
            gradedReferenceFloor = referenceFilter;
            gradedReferenceSignature = signature;
            BindActiveAnchorsToPackageFloor();
            foreach (FloorAnchor anchor in active.GetComponentsInChildren<FloorAnchor>(true))
                anchor.RecacheCorners();
            if (overlayLevel != null && overlayLevel.activeInHierarchy)
                foreach (FloorAnchor anchor in overlayLevel.GetComponentsInChildren<FloorAnchor>(true))
                    anchor.RecacheCorners();
        }

        private static int GradeSignature(MeshFilter floor)
        {
            unchecked
            {
                Mesh mesh = floor.sharedMesh;
                int hash = mesh != null ? mesh.GetInstanceID() : 0;
                if (mesh == null) return hash;
                foreach (Vector3 vertex in mesh.vertices)
                {
                    hash = hash * 31 + Mathf.RoundToInt(vertex.x * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(vertex.y * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(vertex.z * 1000f);
                }
                return hash;
            }
        }

        private void PlayTransition()
        {
            if (transitionEffect == null)
            {
                Debug.LogWarning("[DreamParkPackage] Sequence transition effect is missing from the Overlay Level.", this);
                return;
            }
            // Re-enable from a clean state for every button press. Leaving the
            // looping effect active after StopEmitting made subsequent triggers
            // and its audio Lua dependent on the previous particle state.
            transitionEffect.SetActive(false);
            if (transitionEffect.GetComponent<FloorAnchor>() == null)
                transitionEffect.AddComponent<FloorAnchor>();
            BindAnchorsToLevelFloor(transitionEffect, GetActiveLevel());
            transitionEffect.SetActive(true);
            ParticleSystem[] particles = transitionEffect.GetComponentsInChildren<ParticleSystem>(true);
            if (particles.Length == 0)
                Debug.LogWarning("[DreamParkPackage] Sequence transition has no ParticleSystem.", transitionEffect);
            foreach (var particle in particles)
            {
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                particle.Play(true);
            }
        }

        private void StopTransition()
        {
            if (transitionEffect == null) return;
            foreach (var particle in transitionEffect.GetComponentsInChildren<ParticleSystem>(true))
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            transitionEffect.SetActive(false);
        }

        private void FailChange(int version, int slot, string reason)
        {
            if (version != requestVersion) return;
            changingLevel = false;
            StopTransition();
            lastLevelError = reason;
            LevelChangeFailed?.Invoke(slot);
        }
    }
}
