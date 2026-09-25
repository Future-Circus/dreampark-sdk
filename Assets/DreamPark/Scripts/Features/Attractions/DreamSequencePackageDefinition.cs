using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Small, addressable recipe for a Sequence. The editable levels and Game
    /// Manager remain separate prefabs; ordinary attractions are referenced by
    /// address only so they can still stream one at a time.
    /// </summary>
    public sealed class DreamSequencePackageDefinition : ScriptableObject
    {
        public const string AssetName = "Dream Sequence Package";
        public string sequenceName = "Dream Sequence";
        public GameObject startLevelPrefab;
        public GameObject overlayLevelPrefab;
        public GameObject gameOverLevelPrefab;
        public GameObject gameManagerPrefab;
        public Vector2 minimumRoomMeters;
        [Tooltip("Largest room footprint supported by the included levels' baked growth variants.")]
        public Vector2 maximumRoomMeters;
        public List<DreamSequenceLevel> levels = new List<DreamSequenceLevel>();

        public static string AddressFor(string contentId)
            => contentId + "/Assets/" + AssetName;
    }

    /// <summary>
    /// Assembles a transient single-room package after its definition is
    /// loaded. No per-title Dream Sequence prefab is generated or required.
    /// A park runtime can load this by content id and parent the result under
    /// the same spatial anchor it uses for other AttractionTemplates.
    /// </summary>
    public static class DreamSequencePackageRuntime
    {
        // Returns inactive so the park can set pose and cached/calibrated
        // floorData before LevelTemplate.Start runs. Activate only after both.
        public static async UniTask<GameObject> LoadAsync(string contentId,
            Transform parent = null, bool activate = false)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            DreamSequencePackageDefinition definition = await
                DreamSequencePackageDefinition.AddressFor(contentId)
                    .GetAsset<DreamSequencePackageDefinition>();
            return Create(definition, contentId, parent, activate);
        }

        public static GameObject Create(DreamSequencePackageDefinition definition,
            string runtimeContentId, Transform parent = null, bool activate = false)
        {
            if (definition == null || definition.startLevelPrefab == null
                || definition.overlayLevelPrefab == null || definition.gameOverLevelPrefab == null
                || definition.gameManagerPrefab == null)
            {
                Debug.LogError("[DreamSequence] Package definition is missing Start, Overlay, Game Over, or Game Manager.");
                return null;
            }

            var root = new GameObject(string.IsNullOrEmpty(definition.sequenceName)
                ? "Dream Sequence" : definition.sequenceName);
            root.SetActive(false);
            if (parent != null) root.transform.SetParent(parent, false);
            try
            {
                var sequence = root.AddComponent<DreamSequenceTemplate>();
                sequence.sequenceName = root.name;
                sequence.size = GameLevelSize.Custom;
                sequence.customSize = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
                    DreamSequenceTemplate.StandardLengthFeet);
                sequence.generateFloor = true;
                sequence.gameRequiresAttraction = false;
                sequence.startLevelPrefab = definition.startLevelPrefab;
                sequence.overlayLevelPrefab = definition.overlayLevelPrefab;
                sequence.gameOverLevelPrefab = definition.gameOverLevelPrefab;
                sequence.containerPrefab = definition.gameManagerPrefab;

                AttractionTemplate startTemplate = definition.startLevelPrefab.GetComponent<AttractionTemplate>();
                if (startTemplate != null)
                {
                    sequence.floorMaterial = startTemplate.floorMaterial;
                    sequence.gridDensity = startTemplate.gridDensity;
                    sequence.defaultAnchorPosition = startTemplate.defaultAnchorPosition;
                    sequence.walls = startTemplate.walls;
                }

                var levels = new GameObject("Levels");
                levels.transform.SetParent(root.transform, false);
                GameObject start = UnityEngine.Object.Instantiate(
                    definition.startLevelPrefab, levels.transform);
                start.name = "Level00_Start Level";
                ConfigureLevelFloor(start, true);
                // The Start Level establishes the authored floor/calibration
                // surface. Compute the root bake before adding Overlay VFX or
                // Game Manager geometry, neither of which defines room size.
                Vector2 authored = sequence.DimensionsInFeet * 0.3048f;
                Vector2 minimum = definition.minimumRoomMeters;
                if (minimum.x <= 0f || minimum.y <= 0f) minimum = authored;
                Vector2 maximum = definition.maximumRoomMeters;
                if (maximum.x <= 0f || maximum.y <= 0f) maximum = authored;
                maximum = new Vector2(Mathf.Max(authored.x, maximum.x),
                    Mathf.Max(authored.y, maximum.y));
                sequence.maxGrowthScale = new Vector2(maximum.x / authored.x,
                    maximum.y / authored.y);
                sequence.SetPackingBake(new AttractionPackingBake(
                    authored, sequence.GetSafeFootprintMeters(), minimum,
                    maximum, minimum,
                    new List<AttractionPropPackingPose>()));
                GameObject end = UnityEngine.Object.Instantiate(
                    definition.gameOverLevelPrefab, levels.transform);
                end.name = "Level99_Game Over Level";
                ConfigureLevelFloor(end, true);
                end.SetActive(false);
                GameObject overlay = UnityEngine.Object.Instantiate(
                    definition.overlayLevelPrefab, root.transform);
                overlay.name = "Persistent Overlay Level";
                ConfigureLevelFloor(overlay, false);

                // The overlay UI is hidden over Start and Game Over, but its
                // transition effect must remain available during those swaps.
                Transform authoredEffect = FindRecursive(overlay.transform, "Default Level Transition");
                GameObject transition = null;
                if (authoredEffect != null)
                {
                    authoredEffect.gameObject.SetActive(false);
                    transition = UnityEngine.Object.Instantiate(authoredEffect.gameObject, root.transform);
                    transition.name = "Persistent Level Transition";
                    // This brief, package-owned cue must not be parked by the
                    // venue distance culler mid-transition. The authored copy
                    // stays inactive beneath Overlay; only this runtime copy
                    // is exempt, and it is disabled between level changes.
                    if (transition.GetComponent<OptimizedAFIgnore>() == null)
                        transition.AddComponent<OptimizedAFIgnore>();
                    transition.SetActive(false);
                }
                overlay.SetActive(false);

                GameObject manager = UnityEngine.Object.Instantiate(
                    definition.gameManagerPrefab, root.transform);
                manager.name = "Game Manager";
                // Existing creator-owned Container prefabs are not rewritten by
                // the compiler. Give their runtime instance the shared bus too.
                if (manager.GetComponent<NetId>() == null)
                    manager.AddComponent<NetId>();
                // Existing creator-authored manager prefabs may predate the
                // scaffold marker. Keep this package service running even
                // when its room is beyond the optimizer's distance bands.
                if (manager.GetComponent<OptimizedAFIgnore>() == null)
                    manager.AddComponent<OptimizedAFIgnore>();

                var loader = root.AddComponent<DreamLevelLoader>();
                loader.levelParent = levels.transform;
                sequence.levels = new List<DreamSequenceLevel>();
                foreach (DreamSequenceLevel source in definition.levels ?? new List<DreamSequenceLevel>())
                {
                    if (source == null || string.IsNullOrEmpty(source.address)) continue;
                    string address = RetargetAddress(source.address, runtimeContentId);
                    sequence.levels.Add(new DreamSequenceLevel
                    {
                        sourceGuid = source.sourceGuid,
                        displayName = source.displayName,
                        address = address,
                        occurrenceId = source.occurrenceId,
                        groupOccurrenceId = source.groupOccurrenceId,
                        sourceGroupId = source.sourceGroupId,
                        groupName = source.groupName,
                    });
                    loader.levelAddresses.Add(address);
                }

                var host = root.AddComponent<DreamParkPackageHost>();
                host.kind = DreamParkPackageKind.Sequence;
                host.container = manager;
                host.levelParent = levels.transform;
                host.startLevel = start;
                host.overlayLevel = overlay;
                host.gameOverLevel = end;
                host.transitionEffect = transition;
                host.levelLoader = loader;
                host.transitionDelaySeconds = 2.5f;
                host.overlayAutoVisibility = true;
                if (activate) root.SetActive(true);
                return root;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isPlaying) UnityEngine.Object.Destroy(root);
                else UnityEngine.Object.DestroyImmediate(root);
                return null;
            }
        }

        private static string RetargetAddress(string sourceAddress, string runtimeContentId)
        {
            if (string.IsNullOrEmpty(runtimeContentId)) return sourceAddress;
            int slash = sourceAddress.IndexOf('/');
            return slash >= 0 ? runtimeContentId + sourceAddress.Substring(slash)
                : runtimeContentId + "/" + sourceAddress;
        }

        private static Transform FindRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void ConfigureLevelFloor(GameObject level, bool enabled)
        {
            AttractionTemplate template = level != null ? level.GetComponent<AttractionTemplate>() : null;
            if (template != null) template.generateFloor = enabled;
        }
    }
}
