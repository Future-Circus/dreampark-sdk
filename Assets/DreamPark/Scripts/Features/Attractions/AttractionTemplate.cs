namespace DreamPark
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// One child-prop transform sampled by the attraction packing bake. The bake keeps
    /// transform references (fast at runtime) and stable, human-readable metadata (safe
    /// to publish to the attraction catalog).
    /// </summary>
    [Serializable]
    public sealed class AttractionPropPackingPose
    {
        [SerializeField] private Transform prop;
        [SerializeField] private string hierarchyPath;
        [SerializeField] private string displayName;
        [SerializeField] private string resourceName;
        [SerializeField] private bool essential;
        [SerializeField] private bool authoredActive;
        [SerializeField] private int growGroup;
        [SerializeField] private Vector2 footprintMeters;
        [SerializeField] private Vector3 authoredLocalPosition;
        [SerializeField] private Vector3 shrunkLocalPosition;
        [SerializeField] private Vector3 grownLocalPosition;
        [SerializeField] private Vector3 essentialShrunkLocalPosition;

        public Transform Prop => prop;
        public string HierarchyPath => hierarchyPath ?? "";
        public string DisplayName => displayName ?? "";
        public string ResourceName => resourceName ?? "";
        public bool Essential => essential;
        public bool AuthoredActive => authoredActive;
        public int GrowGroup => growGroup;
        public Vector2 FootprintMeters => footprintMeters;
        public Vector3 AuthoredLocalPosition => authoredLocalPosition;
        public Vector3 ShrunkLocalPosition => shrunkLocalPosition;
        public Vector3 GrownLocalPosition => grownLocalPosition;
        public Vector3 EssentialShrunkLocalPosition => essentialShrunkLocalPosition;

        public AttractionPropPackingPose(
            Transform prop,
            string hierarchyPath,
            string displayName,
            string resourceName,
            bool essential,
            bool authoredActive,
            int growGroup,
            Vector2 footprintMeters,
            Vector3 authoredLocalPosition,
            Vector3 shrunkLocalPosition,
            Vector3 grownLocalPosition,
            Vector3 essentialShrunkLocalPosition)
        {
            this.prop = prop;
            this.hierarchyPath = hierarchyPath;
            this.displayName = displayName;
            this.resourceName = resourceName;
            this.essential = essential;
            this.authoredActive = authoredActive;
            this.growGroup = growGroup;
            this.footprintMeters = footprintMeters;
            this.authoredLocalPosition = authoredLocalPosition;
            this.shrunkLocalPosition = shrunkLocalPosition;
            this.grownLocalPosition = grownLocalPosition;
            this.essentialShrunkLocalPosition = essentialShrunkLocalPosition;
        }
    }

    /// <summary>A non-blocking object's baked attraction-space pose and local scale.</summary>
    [Serializable]
    public sealed class AttractionPackingFollowerPose
    {
        [SerializeField] private Transform follower;
        [SerializeField] private Vector3 authoredPosition;
        [SerializeField] private Vector3 shrunkPosition;
        [SerializeField] private Vector3 grownPosition;
        [SerializeField] private Vector3 essentialShrunkPosition;
        [SerializeField] private Vector3 authoredLocalScale;
        [SerializeField] private Vector3 shrunkLocalScale;
        [SerializeField] private Vector3 grownLocalScale;
        [SerializeField] private Vector3 essentialShrunkLocalScale;
        [SerializeField] private AttractionPackingScaleMode scaleMode;

        public Transform Follower => follower;
        public Vector3 AuthoredPosition => authoredPosition;
        public Vector3 ShrunkPosition => shrunkPosition;
        public Vector3 GrownPosition => grownPosition;
        public Vector3 EssentialShrunkPosition => essentialShrunkPosition;
        public Vector3 AuthoredLocalScale => authoredLocalScale;
        public Vector3 ShrunkLocalScale => shrunkLocalScale;
        public Vector3 GrownLocalScale => grownLocalScale;
        public Vector3 EssentialShrunkLocalScale => essentialShrunkLocalScale;
        public AttractionPackingScaleMode ScaleMode => scaleMode;

        public AttractionPackingFollowerPose(Transform follower, Vector3 authoredPosition,
            Vector3 shrunkPosition, Vector3 grownPosition, Vector3 essentialShrunkPosition,
            Vector3 authoredLocalScale, Vector3 shrunkLocalScale, Vector3 grownLocalScale,
            Vector3 essentialShrunkLocalScale, AttractionPackingScaleMode scaleMode)
        {
            this.follower = follower;
            this.authoredPosition = authoredPosition;
            this.shrunkPosition = shrunkPosition;
            this.grownPosition = grownPosition;
            this.essentialShrunkPosition = essentialShrunkPosition;
            this.authoredLocalScale = authoredLocalScale;
            this.shrunkLocalScale = shrunkLocalScale;
            this.grownLocalScale = grownLocalScale;
            this.essentialShrunkLocalScale = essentialShrunkLocalScale;
            this.scaleMode = scaleMode;
        }
    }

    /// <summary>
    /// Baked footprint ladder for one attraction. All dimensions are local-X/local-Z
    /// metres and remain rectangles so the existing room packer can consume them.
    /// </summary>
    [Serializable]
    public sealed class AttractionPackingBake
    {
        // 10: layout frames (skip-inactive bakes, mirrored Minimum frame).
        // 11: layout-frame bakes record what they were requested for (the declared
        // size, which is always the authored footprint, and the growth multiplier,
        // which chained growth may exceed). 8-11 stay valid.
        public const int CurrentVersion = 11;
        // The catalog receives only footprint/essential-prop data; follower poses
        // are local prefab data. Schema 9 adds the per-frame ladder.
        public const int CatalogSchemaVersion = 9;

        [SerializeField] private int version;
        [SerializeField] private Vector2 authoredFootprintMeters;
        [SerializeField] private Vector2 safeFootprintMeters;
        [SerializeField] private Vector2 shrinkFootprintMeters;
        [SerializeField] private Vector2 growFootprintMeters;
        [SerializeField] private Vector2 essentialShrinkFootprintMeters;
        [SerializeField] private Vector2 shrinkScale = Vector2.one;
        [SerializeField] private Vector2 growScale = Vector2.one;
        [SerializeField] private Vector2 essentialShrinkScale = Vector2.one;
        [SerializeField] private List<AttractionPropPackingPose> props = new List<AttractionPropPackingPose>();
        [SerializeField] private List<AttractionPackingFollowerPose> followers = new List<AttractionPackingFollowerPose>();
        // With layout frames the legacy essential fields describe the Minimum frame
        // (essentialShrink = Minimum's shrink) instead of an essential-props variant.
        [SerializeField] private bool mirrorsMinimumFrame;
        // Symmetric footprint of the packed items exactly as authored (scale 1),
        // measured at bake time. Zero = not measured (bakes older than version 10).
        [SerializeField] private Vector2 layoutFootprintMeters;
        // Layout-frame bakes only: the first collision the bake-time grid check
        // found between two props at some (X, Z) scale pair. Empty = none found.
        [SerializeField] private string layoutIssue;
        // Version 11: the size and growth multiplier this bake was asked for. The
        // authored footprint/grow scale can exceed them (impossible fits, chained
        // growth), so staleness checks compare these instead.
        [SerializeField] private Vector2 requestedFootprintMeters;
        [SerializeField] private Vector2 requestedGrowScale;

        public int Version => version;
        public bool IsValid => version >= 8 && version <= CurrentVersion
            && authoredFootprintMeters.x > 0f && authoredFootprintMeters.y > 0f;
        public Vector2 AuthoredFootprintMeters => authoredFootprintMeters;
        public Vector2 SafeFootprintMeters => safeFootprintMeters;
        public Vector2 ShrinkFootprintMeters => shrinkFootprintMeters;
        public Vector2 GrowFootprintMeters => growFootprintMeters;
        public Vector2 EssentialShrinkFootprintMeters => essentialShrinkFootprintMeters;
        public Vector2 ShrinkScale => shrinkScale;
        public Vector2 GrowScale => growScale;
        public Vector2 EssentialShrinkScale => essentialShrinkScale;
        public IReadOnlyList<AttractionPropPackingPose> Props => props;
        public IReadOnlyList<AttractionPackingFollowerPose> Followers =>
            followers ?? (IReadOnlyList<AttractionPackingFollowerPose>)Array.Empty<AttractionPackingFollowerPose>();
        public bool MirrorsMinimumFrame => mirrorsMinimumFrame;
        public Vector2 LayoutFootprintMeters => layoutFootprintMeters;
        public string LayoutIssue => layoutIssue ?? "";
        /// <summary>The size this bake was requested for (older bakes: the authored footprint).</summary>
        public Vector2 RequestedFootprintMeters =>
            version >= 11 && requestedFootprintMeters.x > 0f && requestedFootprintMeters.y > 0f
                ? requestedFootprintMeters
                : authoredFootprintMeters;
        /// <summary>The growth multiplier this bake was requested for (older bakes: the grow scale).</summary>
        public Vector2 RequestedGrowScale =>
            version >= 11 && requestedGrowScale.x > 0f && requestedGrowScale.y > 0f
                ? requestedGrowScale
                : growScale;
        public bool HasEssentialProps
        {
            get
            {
                if (mirrorsMinimumFrame) return true;
                if (props == null) return false;
                for (int i = 0; i < props.Count; i++)
                    if (props[i] != null && props[i].Essential) return true;
                return false;
            }
        }

        public AttractionPackingBake(
            Vector2 authored,
            Vector2 safe,
            Vector2 shrink,
            Vector2 grow,
            Vector2 essentialShrink,
            List<AttractionPropPackingPose> props,
            List<AttractionPackingFollowerPose> followers = null)
        {
            version = CurrentVersion;
            authoredFootprintMeters = authored;
            safeFootprintMeters = safe;
            shrinkFootprintMeters = shrink;
            growFootprintMeters = grow;
            essentialShrinkFootprintMeters = essentialShrink;
            shrinkScale = DivideFootprints(shrink, authored);
            growScale = DivideFootprints(grow, authored);
            essentialShrinkScale = DivideFootprints(essentialShrink, authored);
            this.props = props ?? new List<AttractionPropPackingPose>();
            this.followers = followers ?? new List<AttractionPackingFollowerPose>();
            requestedFootprintMeters = authored;
            requestedGrowScale = growScale;
        }

        public AttractionPackingBake(
            Vector2 authored,
            Vector2 safe,
            Vector2 shrink,
            Vector2 grow,
            Vector2 essentialShrink,
            List<AttractionPropPackingPose> props,
            List<AttractionPackingFollowerPose> followers,
            bool mirrorsMinimumFrame)
            : this(authored, safe, shrink, grow, essentialShrink, props, followers)
        {
            this.mirrorsMinimumFrame = mirrorsMinimumFrame;
        }

        /// <summary>Editor/baker hook: record the measured authored item footprint.</summary>
        public void SetLayoutFootprintMeters(Vector2 value) => layoutFootprintMeters = value;

        /// <summary>Editor/baker hook: record what this bake was asked for (see RequestedFootprintMeters).</summary>
        public void SetRequested(Vector2 footprintMeters, Vector2 growScaleMultiplier)
        {
            requestedFootprintMeters = footprintMeters;
            requestedGrowScale = growScaleMultiplier;
        }

        /// <summary>Editor/baker hook: record the bake-time collision check result.</summary>
        public void SetLayoutIssue(string value) => layoutIssue = value ?? "";

        private static Vector2 DivideFootprints(Vector2 value, Vector2 authored)
        {
            return new Vector2(
                authored.x > 0f ? value.x / authored.x : 1f,
                authored.y > 0f ? value.y / authored.y : 1f);
        }
    }

    /// <summary>
    /// Attractions are self-contained experiences. In addition to LevelTemplate's
    /// authored rectangle, this component carries a baked family of layouts that can
    /// be selected with independent local-X/local-Z scale multipliers and an
    /// essential-only toggle. A scale of 1 is always the authored layout.
    /// </summary>
    public class AttractionTemplate : LevelTemplate
    {
        /// <summary>The implicit authored layout (never stored in the frame list).</summary>
        public const string BaseFrameId = "base";
        /// <summary>Fixed id of the at-most-one Minimum frame.</summary>
        public const string MinimumFrameId = "minimum";
        // A frame "fits" a target when its shrink is within this of the target.
        private const float FrameFitEpsilon = 1e-4f;

        // Uploaded per attraction by the post-publish catalog metadata push
        // (ContentUploaderPanel → /attractions/dimensions, `displayName`). The
        // catalog stores it as `authoredName` and "Sync Attractions" keeps it;
        // a rename in the developer portal (nameOverridden) still wins.
        [Header("Catalog")]
        [Tooltip("Player-facing name shown in the DreamPark apps (phone attraction picker, park pages). Leave empty to let the platform derive one from the prefab name (A_LT_RW_Lobby → \"LT RW Lobby\"). A rename in the developer portal still wins.")]
        public string displayName = "";

        [Header("Game Placement")]
        [Tooltip("The game cannot function without this attraction. Park generation must include it, or fall back to the game's Dream Sequence when the physical layout cannot fit every required attraction.")]
        public bool gameRequiresAttraction;

        [Header("Flexible Packing")]
        [UnityEngine.Serialization.FormerlySerializedAs("safeArea")]
        [Range(0f, 0.49f)]
        [Tooltip("Normalized inset from each edge of the authored attraction. 0 matches the full AttractionTemplate bounds; larger values move the safe boundary farther inside. This does not limit the independently baked shrink range.")]
        public float safeAreaInset = 0f;

        // Source compatibility for code written against the original field name.
        // Unity serializes safeAreaInset; FormerlySerializedAs migrates existing assets.
        public float safeArea
        {
            get => safeAreaInset;
            set => safeAreaInset = value;
        }

        // Superseded by the Minimum layout frame. Kept (hidden) so the baker can
        // migrate it once into a Minimum frame; see AttractionPackingBaker.
        [HideInInspector]
        [Tooltip("Child prop roots that the attraction cannot function without. References may point anywhere inside a nested PropTemplate; the baker resolves the owning prop root.")]
        public List<Transform> essentialProps = new List<Transform>();

        [SerializeField, HideInInspector]
        public Vector2 maxGrowthScale = new Vector2(1.25f, 1.25f);

        [SerializeField, HideInInspector, Min(0f)]
        [Tooltip("Minimum edge-to-edge clearance preserved while props are pulled inward, in metres.")]
        public float shrinkClearanceMeters = 0.08f;

        [SerializeField, HideInInspector, Min(0f)]
        [Tooltip("Props this close together are grouped during growth. Compact mixed groups stay rigid; repeated aligned runs may spread along their run axis.")]
        public float growGroupGapMeters = 0.35f;

        [SerializeField, HideInInspector, Min(0f)]
        [Tooltip("Internal alignment tolerance used to recognize nearby growth groups and repeated aligned prop runs.")]
        public float growAlignmentToleranceMeters = 0.12f;

        [SerializeField, HideInInspector]
        private AttractionPackingBake packingBake;

        [SerializeField, HideInInspector]
        private Vector2 packingPreviewScale = Vector2.one;

        [SerializeField, HideInInspector]
        private bool packingPreviewEssentialOnly;

        [SerializeField, HideInInspector]
        private bool showPackingGizmos = true;

        // Hand-authored layouts at other sizes. Base is implicit (packingBake).
        [SerializeField, HideInInspector]
        private List<AttractionLayoutFrame> layoutFrames = new List<AttractionLayoutFrame>();

        // Base (authored) state of every transform a frame or bake may touch.
        [SerializeField, HideInInspector]
        private List<AttractionLayoutBaseState> layoutBaseStates = new List<AttractionLayoutBaseState>();

        [SerializeField, HideInInspector]
        private bool legacyEssentialMigrated;

        // Base entries/exits on the footprint edge (see AttractionPortal). The
        // initializer is also what prefabs saved before this field existed load
        // with: every attraction starts with one entry at the middle of -Z.
        [SerializeField, HideInInspector]
        private List<AttractionPortal> portals = AttractionPortal.DefaultList();

        // Optional per-side extras, used only where core reports a real wall
        // (WALL_LAYERS_SPEC.md). LIST ORDER IS PRIORITY: an object several layers
        // move goes to the first walled layer in this list.
        [SerializeField, HideInInspector]
        private List<AttractionWallLayer> wallLayers = new List<AttractionWallLayer>();

        // Editor debug preview of walled sides (Play Mode applies it).
        [SerializeField, HideInInspector]
        private AttractionWallMask layoutPreviewWalls;

        // Editor debug preview for frame attractions: zero = unset, empty id = auto.
        [SerializeField, HideInInspector]
        private Vector2 layoutPreviewFootprintMeters;

        [SerializeField, HideInInspector]
        private string layoutPreviewFrameId;

        [NonSerialized] private bool hasRuntimePackingFootprint;
        [NonSerialized] private Vector2 runtimePackingFootprintMeters;
        [NonSerialized] private bool runtimeFootprintFromFrame;
        [NonSerialized] private bool hasCurrentLayout;
        [NonSerialized] private string currentFrameId;
        [NonSerialized] private Vector2 currentFrameScale;
        [NonSerialized] private HashSet<string> warnedLayoutFrameIds;
        [NonSerialized] private AttractionWallMask currentWalls;
        // What the park (core) asked for; an editor walls preview layers over it.
        [NonSerialized] private AttractionWallMask parkWalls;
        [NonSerialized] private bool wallsAppliedOnce;

        public AttractionPackingBake PackingBake => packingBake;
        /// <summary>
        /// A bake is only usable while it still describes this attraction's authored
        /// footprint and growth settings. Treating an old bake as valid after Custom
        /// Size changes makes 1.0 refer to the previous rectangle and leaves the debug
        /// controls apparently stuck on their old limits.
        /// </summary>
        public bool HasPackingBake => packingBake != null
            && packingBake.IsValid
            && Approximately(packingBake.RequestedFootprintMeters, AuthoredFootprintMeters)
            && Approximately(packingBake.RequestedGrowScale, RequestedGrowScale);
        public bool HasStalePackingBake => packingBake != null && packingBake.IsValid && !HasPackingBake;
        public Vector2 PackingPreviewScale => packingPreviewScale;
        public bool PackingPreviewEssentialOnly => packingPreviewEssentialOnly;
        public bool ShowPackingGizmos => showPackingGizmos;
        public override Vector2 RuntimeFootprintMeters =>
            (HasPackingBake || runtimeFootprintFromFrame) && hasRuntimePackingFootprint
                ? runtimePackingFootprintMeters
                : base.RuntimeFootprintMeters;

        public IReadOnlyList<AttractionLayoutFrame> LayoutFrames =>
            layoutFrames ?? (IReadOnlyList<AttractionLayoutFrame>)Array.Empty<AttractionLayoutFrame>();
        public IReadOnlyList<AttractionLayoutBaseState> LayoutBaseStates =>
            layoutBaseStates ?? (IReadOnlyList<AttractionLayoutBaseState>)Array.Empty<AttractionLayoutBaseState>();

        /// <summary>True when at least one Custom or Minimum frame exists.</summary>
        public bool HasLayoutFrames
        {
            get
            {
                if (layoutFrames == null) return false;
                for (int i = 0; i < layoutFrames.Count; i++)
                    if (layoutFrames[i] != null) return true;
                return false;
            }
        }

        /// <summary>The Minimum frame, or null when the attraction has none.</summary>
        public AttractionLayoutFrame MinimumFrame
        {
            get
            {
                if (layoutFrames == null) return null;
                for (int i = 0; i < layoutFrames.Count; i++)
                {
                    AttractionLayoutFrame frame = layoutFrames[i];
                    if (frame != null && frame.kind == AttractionLayoutFrameKind.Minimum) return frame;
                }
                return null;
            }
        }

        public bool LegacyEssentialMigrated
        {
            get => legacyEssentialMigrated;
            set => legacyEssentialMigrated = value;
        }

        /// <summary>Runtime: the frame last applied ("base" until something else is).</summary>
        public string CurrentFrameId => hasCurrentLayout && !string.IsNullOrEmpty(currentFrameId)
            ? currentFrameId
            : BaseFrameId;
        /// <summary>Runtime: the clamped frame scale last applied, relative to that frame.</summary>
        public Vector2 CurrentFrameScale => hasCurrentLayout ? currentFrameScale : Vector2.one;
        public Vector2 LayoutPreviewFootprintMeters => layoutPreviewFootprintMeters;
        public string LayoutPreviewFrameId => layoutPreviewFrameId ?? "";
        public bool HasLayoutPreview =>
            (layoutPreviewFootprintMeters.x > 0f && layoutPreviewFootprintMeters.y > 0f)
            || !string.IsNullOrEmpty(layoutPreviewFrameId)
            || layoutPreviewWalls != AttractionWallMask.None;
        public AttractionWallMask LayoutPreviewWalls => layoutPreviewWalls;

        private Vector2 AuthoredFootprintMeters
        {
            get
            {
                Vector3 authored = Size;
                return new Vector2(authored.x, authored.z);
            }
        }

        private Vector2 RequestedGrowScale => new Vector2(
            Mathf.Max(1f, maxGrowthScale.x),
            Mathf.Max(1f, maxGrowthScale.y));

        private static bool Approximately(Vector2 a, Vector2 b) =>
            (a - b).sqrMagnitude <= 0.000001f;

        /// <summary>
        /// Dream Sequences use a standard 12 ft x 18 ft room. An attraction is
        /// compatible when its authored footprint fits that room, or when the
        /// flexible-packing bake proves a larger attraction can shrink into that same
        /// 12 ft x 18 ft footprint. Rotation is allowed in both cases.
        /// </summary>
        public bool IsDreamSequenceCompatible => DreamSequenceCompatibility.IsCompatible(this);

        /// <summary>
        /// Resolve the rectangular footprint the packer must reserve. packingScale is
        /// a direct authored-size multiplier on local X/Z: (1,1) is authored size and
        /// (0.89,1.1) is exactly 89% width by 110% length.
        /// </summary>
        public Vector2 GetPackingFootprintMeters(Vector2 packingScale, bool essentialOnly)
        {
            // With layout frames, the legacy essential variant IS the Minimum frame.
            if (HasLayoutFrames)
            {
                MapLegacyVariant(packingScale, essentialOnly, out string frameId, out Vector2 frameScale);
                return GetFrameFootprintMeters(frameId, frameScale);
            }
            return GetBaseFootprintMeters(packingScale, essentialOnly);
        }

        private Vector2 GetBaseFootprintMeters(Vector2 packingScale, bool essentialOnly)
        {
            Vector2 authored = HasPackingBake
                ? packingBake.AuthoredFootprintMeters
                : new Vector2(Size.x, Size.z);

            if (!HasPackingBake) return authored;

            Vector2 minimum = essentialOnly && packingBake.HasEssentialProps
                ? packingBake.EssentialShrinkScale
                : packingBake.ShrinkScale;
            Vector2 maximum = packingBake.GrowScale;
            Vector2 clampedScale = new Vector2(
                Mathf.Clamp(packingScale.x, minimum.x, maximum.x),
                Mathf.Clamp(packingScale.y, minimum.y, maximum.y));
            Vector2 result = Vector2.Scale(authored, clampedScale);
            return result;
        }

        [Obsolete("Safe area is independent metadata. Use GetPackingFootprintMeters(Vector2, bool) and GetSafeFootprintMeters(float) separately.")]
        public Vector2 GetPackingFootprintMeters(Vector2 packingScale, bool essentialOnly, float ignoredSafeArea)
            => GetPackingFootprintMeters(packingScale, essentialOnly);

        /// <summary>
        /// Safe-area visualization derived as an inset from every edge. It is an
        /// authoring/placement signal, not a clamp on the shrink bake.
        /// </summary>
        public Vector2 GetSafeFootprintMeters(float insetOverride = -1f)
        {
            Vector2 authored = HasPackingBake
                ? packingBake.AuthoredFootprintMeters
                : new Vector2(Size.x, Size.z);
            float inset = insetOverride >= 0f ? insetOverride : safeAreaInset;
            float remaining = 1f - Mathf.Clamp(inset, 0f, 0.49f) * 2f;
            return authored * remaining;
        }

        /// <summary>
        /// Compatibility bridge for callers using the original normalized control.
        /// New code should pass direct X/Z multipliers to the Vector2 overload.
        /// </summary>
        [Obsolete("Use GetPackingFootprintMeters(Vector2, bool). Packing scale is now a direct X/Z multiplier.")]
        public Vector2 GetPackingFootprintMeters(int growthShrink, bool essentialOnly)
        {
            if (!HasPackingBake) return new Vector2(Size.x, Size.z);
            float t = Mathf.Clamp(growthShrink, -100, 100) / 100f;
            Vector2 minimum = essentialOnly && packingBake.HasEssentialProps
                ? packingBake.EssentialShrinkScale
                : packingBake.ShrinkScale;
            Vector2 scale = t < 0f
                ? Vector2.Lerp(Vector2.one, minimum, -t)
                : Vector2.Lerp(Vector2.one, packingBake.GrowScale, t);
            return GetPackingFootprintMeters(scale, essentialOnly);
        }

        [Obsolete("Safe area is independent metadata. Use GetPackingFootprintMeters(Vector2, bool) and GetSafeFootprintMeters(float) separately.")]
        public Vector2 GetPackingFootprintMeters(int growthShrink, bool essentialOnly, float ignoredSafeArea)
            => GetPackingFootprintMeters(growthShrink, essentialOnly);

        /// <summary>
        /// Apply one baked layout without doing geometry work at runtime. Non-essential
        /// prop roots are disabled only for the essential variant and restored to their
        /// authored active state when that mode is left.
        /// </summary>
        public bool ApplyPackingVariant(Vector2 packingScale, bool essentialOnly)
            => ApplyPackingVariantInternal(packingScale, essentialOnly, false);

        private bool ApplyPackingVariantInternal(
            Vector2 packingScale,
            bool essentialOnly,
            bool replaceGeneratedSurfacesImmediately)
        {
            if (UsesLayoutPipeline)
            {
                // Legacy callers (old park documents, Sequence, previews) keep
                // working: essentialOnly selects the Minimum frame, else Base.
                MapLegacyVariant(packingScale, essentialOnly, out string frameId, out Vector2 frameScale);
                return ApplyLayoutInternal(frameId, frameScale, replaceGeneratedSurfacesImmediately);
            }
            if (!HasPackingBake) return false;

            bool useEssential = essentialOnly && packingBake.HasEssentialProps;
            Vector2 nextFootprint = GetPackingFootprintMeters(packingScale, useEssential);
            bool footprintChanged = !hasRuntimePackingFootprint
                || (runtimePackingFootprintMeters - nextFootprint).sqrMagnitude > 0.000001f;
            runtimePackingFootprintMeters = nextFootprint;
            hasRuntimePackingFootprint = true;
            SetCurrentLayout(BaseFrameId, DivideFootprint(nextFootprint, packingBake.AuthoredFootprintMeters));
            var poses = packingBake.Props;
            for (int i = 0; i < poses.Count; i++)
            {
                AttractionPropPackingPose pose = poses[i];
                if (pose == null) continue;
                Transform prop = pose.Prop;
                if (prop == null) continue;

                bool active = !useEssential || pose.Essential;
                prop.gameObject.SetActive(active && pose.AuthoredActive);
                prop.localPosition = GetPackingPoseLocalPosition(pose, packingScale, useEssential);
            }
            IReadOnlyList<AttractionPackingFollowerPose> followers = packingBake.Followers;
            List<FloorAnchor> anchorsToRecache = null;
            for (int i = 0; i < followers.Count; i++)
            {
                AttractionPackingFollowerPose pose = followers[i];
                Transform follower = pose?.Follower;
                if (follower == null) continue;
                Vector3 position = GetFollowerPosition(packingBake, pose, packingScale, useEssential);
                Vector3 worldPosition = transform.TransformPoint(position);
                Vector3 localScale = GetFollowerLocalScale(packingBake, pose, packingScale, useEssential);
                bool changed = (follower.position - worldPosition).sqrMagnitude > 0.0000001f
                    || (follower.localScale - localScale).sqrMagnitude > 0.0000001f;
                if (!changed) continue;
                follower.position = worldPosition;
                follower.localScale = localScale;
                FloorAnchor anchor = follower.GetComponent<FloorAnchor>();
                if (anchor == null) continue;
                if (anchorsToRecache == null) anchorsToRecache = new List<FloorAnchor>();
                anchorsToRecache.Add(anchor);
            }
            RefreshPackingDependentSystems(footprintChanged, replaceGeneratedSurfacesImmediately);
            if (anchorsToRecache != null)
                for (int i = 0; i < anchorsToRecache.Count; i++)
                    anchorsToRecache[i].RecacheCorners();
            NotifyLayoutApplied();
            return true;
        }

        private void RefreshPackingDependentSystems(
            bool footprintChanged,
            bool replaceGeneratedSurfacesImmediately)
        {
            GetComponent<GameArea>()?.ComputeBounds();
            GetComponent<MusicArea>()?.ComputeBounds();

            // The shipping loader applies packing before Start, so its first floor
            // is already the right size. This branch covers editor slider changes
            // and fallback/legacy callers that select a variant after startup.
            if (!footprintChanged || !Application.isPlaying) return;
#if UNITY_EDITOR
            if (replaceGeneratedSurfacesImmediately)
            {
                if (runtimePlane != null) RegenerateFloorForEditorPreview();
                if (runtimeCeiling != null) RegenerateCeilingForEditorPreview();
                return;
            }
#endif
            if (runtimePlane != null)
            {
                CalibrateLevel previous = runtimePlane.GetComponent<CalibrateLevel>();
                Vector3[] grade = previous != null ? previous.CaptureGradeWorldSamples() : null;
                RegenerateFloor();
                CalibrateLevel current = runtimePlane != null
                    ? runtimePlane.GetComponent<CalibrateLevel>() : null;
                if (current != null && current != previous && current.TransferGradeFrom(grade))
                {
                    floorData = current.CompileCalibrationData();
                    // The transferred mesh is already live. Do not let the new
                    // CalibrateLevel's Start queue the old topology for replay.
                    current.floorData = null;
                }
            }
            if (runtimeCeiling != null) RegenerateCeiling();
        }

        public Vector3 GetPackingPoseLocalPosition(
            AttractionPropPackingPose pose,
            Vector2 packingScale,
            bool essentialOnly)
        {
            if (pose == null || !HasPackingBake) return Vector3.zero;
            bool useEssential = essentialOnly && packingBake.HasEssentialProps;
            Vector2 minimumScale = useEssential
                ? packingBake.EssentialShrinkScale
                : packingBake.ShrinkScale;
            Vector2 scale = new Vector2(
                Mathf.Clamp(packingScale.x, minimumScale.x, packingBake.GrowScale.x),
                Mathf.Clamp(packingScale.y, minimumScale.y, packingBake.GrowScale.y));
            Vector3 minimum = useEssential && pose.Essential
                ? pose.EssentialShrunkLocalPosition
                : pose.ShrunkLocalPosition;
            return ResolvePose(
                pose.AuthoredLocalPosition,
                minimum,
                pose.GrownLocalPosition,
                scale,
                minimumScale,
                packingBake.GrowScale);
        }

        private static Vector3 GetFollowerPosition(AttractionPackingBake bake,
            AttractionPackingFollowerPose pose, Vector2 packingScale, bool essentialOnly)
        {
            Vector2 minimum = essentialOnly ? bake.EssentialShrinkScale : bake.ShrinkScale;
            Vector2 scale = new Vector2(
                Mathf.Clamp(packingScale.x, minimum.x, bake.GrowScale.x),
                Mathf.Clamp(packingScale.y, minimum.y, bake.GrowScale.y));
            return ResolvePose(pose.AuthoredPosition,
                essentialOnly ? pose.EssentialShrunkPosition : pose.ShrunkPosition,
                pose.GrownPosition, scale, minimum, bake.GrowScale);
        }

        private static Vector3 GetFollowerLocalScale(AttractionPackingBake bake,
            AttractionPackingFollowerPose pose, Vector2 packingScale, bool essentialOnly)
        {
            Vector2 minimum = essentialOnly ? bake.EssentialShrinkScale : bake.ShrinkScale;
            Vector3 low = essentialOnly ? pose.EssentialShrunkLocalScale : pose.ShrunkLocalScale;
            Vector3 authored = pose.AuthoredLocalScale;
            Vector3 high = pose.GrownLocalScale;
            float tx = AxisProgress(packingScale.x, minimum.x, bake.GrowScale.x);
            float tz = AxisProgress(packingScale.y, minimum.y, bake.GrowScale.y);
            if (pose.ScaleMode == AttractionPackingScaleMode.XZIndependent)
                return new Vector3(
                    tx < 0f ? Mathf.Lerp(authored.x, low.x, -tx) : Mathf.Lerp(authored.x, high.x, tx),
                    authored.y,
                    tz < 0f ? Mathf.Lerp(authored.z, low.z, -tz) : Mathf.Lerp(authored.z, high.z, tz));

            float progress = Mathf.Min(tx, tz);
            return progress < 0f
                ? Vector3.Lerp(authored, low, -progress)
                : Vector3.Lerp(authored, high, progress);
        }

        private static float AxisProgress(float scale, float minimum, float maximum)
        {
            scale = Mathf.Clamp(scale, minimum, maximum);
            if (scale < 1f && minimum < 1f)
                return -Mathf.InverseLerp(1f, minimum, scale);
            if (scale > 1f && maximum > 1f)
                return Mathf.InverseLerp(1f, maximum, scale);
            return 0f;
        }

        [Obsolete("Use ApplyPackingVariant(Vector2, bool). Packing scale is now a direct X/Z multiplier.")]
        public bool ApplyPackingVariant(int growthShrink, bool essentialOnly)
        {
            if (!HasPackingBake) return false;
            float t = Mathf.Clamp(growthShrink, -100, 100) / 100f;
            Vector2 minimum = essentialOnly && packingBake.HasEssentialProps
                ? packingBake.EssentialShrinkScale
                : packingBake.ShrinkScale;
            Vector2 scale = t < 0f
                ? Vector2.Lerp(Vector2.one, minimum, -t)
                : Vector2.Lerp(Vector2.one, packingBake.GrowScale, t);
            return ApplyPackingVariant(scale, essentialOnly);
        }

        private static Vector3 ResolvePose(
            Vector3 authored,
            Vector3 shrunk,
            Vector3 grown,
            Vector2 scale,
            Vector2 minimumScale,
            Vector2 maximumScale)
        {
            float x = ResolveAxis(authored.x, shrunk.x, grown.x, scale.x, minimumScale.x, maximumScale.x);
            float z = ResolveAxis(authored.z, shrunk.z, grown.z, scale.y, minimumScale.y, maximumScale.y);
            return new Vector3(x, authored.y, z);
        }

        private static float ResolveAxis(
            float authored,
            float shrunk,
            float grown,
            float scale,
            float minimumScale,
            float maximumScale)
        {
            if (scale < 1f && minimumScale < 1f)
                return Mathf.Lerp(authored, shrunk, Mathf.InverseLerp(1f, minimumScale, scale));
            if (scale > 1f && maximumScale > 1f)
                return Mathf.Lerp(authored, grown, Mathf.InverseLerp(1f, maximumScale, scale));
            return authored;
        }

        /// <summary>Restore every baked child prop to its authored pose and active state.</summary>
        public void ResetPackingVariant()
        {
            packingPreviewScale = Vector2.one;
            packingPreviewEssentialOnly = false;
            layoutPreviewFootprintMeters = Vector2.zero;
            layoutPreviewFrameId = "";
            ApplyPackingVariant(Vector2.one, false);
        }

        public void SetPackingPreview(Vector2 scale, bool essentialOnly)
        {
            packingPreviewScale = scale;
            packingPreviewEssentialOnly = essentialOnly;
#if UNITY_EDITOR
            // Edit Mode keeps the authored hierarchy untouched and draws ghosts.
            // In Play Mode the debug controls intentionally drive the real baked
            // transforms so creators can exercise the adjusted attraction.
            if (Application.isPlaying)
                ApplyPackingVariantInternal(packingPreviewScale, packingPreviewEssentialOnly, true);
#endif
        }

#if UNITY_EDITOR
        private void Awake()
        {
            if (!Application.isPlaying) return;
            if (UsesLayoutPipeline)
            {
                // Same rule as the legacy (1,1) default: an unset layout preview
                // leaves the real park packing decision untouched.
                if (HasLayoutPreview) ApplyLayoutPreview();
                return;
            }
            if (!HasPackingBake) return;
            if (packingPreviewScale == Vector2.one && !packingPreviewEssentialOnly) return;
            ApplyPackingVariantInternal(packingPreviewScale, packingPreviewEssentialOnly, true);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            if (UsesLayoutPipeline)
            {
                if (HasLayoutPreview) ApplyLayoutPreview();
                return;
            }
            if (!HasPackingBake) return;
            if (packingPreviewScale == Vector2.one && !packingPreviewEssentialOnly) return;

            // Run after the park loader's Update. A non-default editor preview is
            // an explicit local override; the default (1,1) leaves real park
            // packingScale/essentialOnly decisions completely untouched.
            ApplyPackingVariantInternal(packingPreviewScale, packingPreviewEssentialOnly, true);
        }
#endif

        // ---------------------------------------------------------------------
        // Layout frames. Each frame is a complete hand-authored arrangement at one
        // width × length with its own shrink/grow bake. Runtime picks ONE frame for
        // the space it was given and scales within that frame's own range; frames
        // never blend. Attractions without frames keep the legacy paths above.
        // ---------------------------------------------------------------------

        /// <summary>A Custom or Minimum frame by id; null for "base" and unknown ids.</summary>
        public AttractionLayoutFrame FindFrame(string id)
        {
            if (string.IsNullOrEmpty(id) || id == BaseFrameId || layoutFrames == null) return null;
            if (id == MinimumFrameId) return MinimumFrame;
            for (int i = 0; i < layoutFrames.Count; i++)
            {
                AttractionLayoutFrame frame = layoutFrames[i];
                if (frame != null && frame.kind == AttractionLayoutFrameKind.Custom && frame.id == id)
                    return frame;
            }
            return null;
        }

        /// <summary>Frame id syntax shared with Web and iOS: ^[a-z0-9][a-z0-9-]{0,31}$.</summary>
        public static bool IsValidFrameId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) continue;
                if (c == '-' && i > 0) continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// A frame bake is usable only while it was requested for that frame's size
        /// and the attraction's growth settings (the same rule as HasPackingBake). Its
        /// authored footprint can be larger than the size when the content cannot pack
        /// into it; that is not stale.
        /// </summary>
        public bool FrameHasValidBake(AttractionLayoutFrame frame)
        {
            AttractionPackingBake bake = frame?.Bake;
            return bake != null
                && bake.IsValid
                && Approximately(bake.RequestedFootprintMeters, frame.SizeMeters)
                && Approximately(bake.RequestedGrowScale, RequestedGrowScale);
        }

        /// <summary>
        /// The ladder: every frame envelope with a valid bake, largest → smallest
        /// authored area (ties: larger short side, then base > custom > minimum,
        /// then list order). Empty without a valid Base bake; Base only without frames.
        /// </summary>
        public List<AttractionFrameEnvelope> GetFrameEnvelopes()
        {
            var envelopes = new List<AttractionFrameEnvelope>();
            if (!HasPackingBake) return envelopes;

            var listOrder = new List<int>();
            envelopes.Add(EnvelopeFor(BaseFrameId, "base", "Base", packingBake));
            listOrder.Add(-1);
            if (layoutFrames != null)
            {
                var seen = new HashSet<string> { BaseFrameId };
                for (int i = 0; i < layoutFrames.Count; i++)
                {
                    AttractionLayoutFrame frame = layoutFrames[i];
                    if (!IsLadderFrame(frame)) continue;
                    string id = FrameIdOf(frame);
                    if (!seen.Add(id)) continue;
                    bool minimum = frame.kind == AttractionLayoutFrameKind.Minimum;
                    envelopes.Add(EnvelopeFor(id, minimum ? "minimum" : "custom",
                        string.IsNullOrEmpty(frame.name) ? id : frame.name, frame.Bake));
                    listOrder.Add(i);
                }
            }
            if (envelopes.Count == 1) return envelopes;

            var indices = new int[envelopes.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) =>
            {
                AttractionFrameEnvelope x = envelopes[a];
                AttractionFrameEnvelope y = envelopes[b];
                // Sizes come from feet, so equal frames can differ by float noise.
                float areaX = Area(x.authoredMeters);
                float areaY = Area(y.authoredMeters);
                if (Mathf.Abs(areaX - areaY) > 0.0001f) return areaY.CompareTo(areaX);
                float sideX = Mathf.Min(x.authoredMeters.x, x.authoredMeters.y);
                float sideY = Mathf.Min(y.authoredMeters.x, y.authoredMeters.y);
                if (Mathf.Abs(sideX - sideY) > 0.0001f) return sideY.CompareTo(sideX);
                int rank = KindRank(x.kind).CompareTo(KindRank(y.kind));
                return rank != 0 ? rank : listOrder[a].CompareTo(listOrder[b]);
            });
            var ladder = new List<AttractionFrameEnvelope>(envelopes.Count);
            for (int i = 0; i < indices.Length; i++) ladder.Add(envelopes[indices[i]]);
            return ladder;
        }

        /// <summary>One frame's envelope ("base"/empty = Base). False when it has no valid bake.</summary>
        public bool TryGetFrameEnvelope(string frameId, out AttractionFrameEnvelope envelope)
        {
            if (IsBaseFrameId(frameId))
            {
                envelope = HasPackingBake
                    ? EnvelopeFor(BaseFrameId, "base", "Base", packingBake)
                    : default;
                return HasPackingBake;
            }
            AttractionLayoutFrame frame = FindFrame(frameId);
            if (!IsLadderFrame(frame))
            {
                envelope = default;
                return false;
            }
            bool minimum = frame.kind == AttractionLayoutFrameKind.Minimum;
            envelope = EnvelopeFor(FrameIdOf(frame), minimum ? "minimum" : "custom",
                string.IsNullOrEmpty(frame.name) ? FrameIdOf(frame) : frame.name, frame.Bake);
            return true;
        }

        /// <summary>
        /// The smallest footprint this attraction can ever occupy: the smallest-area
        /// shrink across the ladder (legacy: shrink, or the essential variant's shrink
        /// when that is smaller). Authored size when nothing is baked.
        /// </summary>
        public Vector2 MinimumFootprintMeters
        {
            get
            {
                if (!HasPackingBake) return AuthoredFootprintMeters;
                if (!HasLayoutFrames)
                {
                    Vector2 shrink = packingBake.ShrinkFootprintMeters;
                    if (!packingBake.HasEssentialProps) return shrink;
                    Vector2 essential = packingBake.EssentialShrinkFootprintMeters;
                    return Area(essential) < Area(shrink) ? essential : shrink;
                }
                List<AttractionFrameEnvelope> ladder = GetFrameEnvelopes();
                return ladder[SmallestShrinkIndex(ladder)].shrinkMeters;
            }
        }

        /// <summary>
        /// Pick the largest frame whose shrink fits targetMeters (prefab-local X/Z;
        /// callers rotate first). frameScale = target / authored, clamped to that
        /// frame's range. When nothing fits, returns false with the smallest-shrink
        /// frame at its shrink scale.
        /// </summary>
        public bool TrySelectFrame(Vector2 targetMeters, out string frameId, out Vector2 frameScale)
        {
            List<AttractionFrameEnvelope> ladder = GetFrameEnvelopes();
            if (ladder.Count == 0)
            {
                Vector2 authored = AuthoredFootprintMeters;
                frameId = BaseFrameId;
                frameScale = Vector2.one;
                return authored.x <= targetMeters.x + FrameFitEpsilon
                    && authored.y <= targetMeters.y + FrameFitEpsilon;
            }

            for (int i = 0; i < ladder.Count; i++)
            {
                AttractionFrameEnvelope envelope = ladder[i];
                if (envelope.shrinkMeters.x > targetMeters.x + FrameFitEpsilon
                    || envelope.shrinkMeters.y > targetMeters.y + FrameFitEpsilon) continue;
                frameId = envelope.frameId;
                frameScale = ClampScale(DivideFootprint(targetMeters, envelope.authoredMeters),
                    envelope.shrinkScale, envelope.growScale);
                return true;
            }

            AttractionFrameEnvelope fallback = ladder[SmallestShrinkIndex(ladder)];
            frameId = fallback.frameId;
            frameScale = fallback.shrinkScale;
            return false;
        }

        /// <summary>
        /// Apply one frame at a frame-relative scale. Every recorded Base state is
        /// restored first, so A → B → A on a live attraction is exact. Unknown or
        /// unbaked frame ids warn once and apply Base. Returns false only when no
        /// baked layout could be applied (the authored hierarchy is still restored).
        /// </summary>
        public bool ApplyLayout(string frameId, Vector2 frameScale)
            => ApplyLayoutInternal(frameId, frameScale, false);

        /// <summary>TrySelectFrame + ApplyLayout. The fallback frame is applied even when nothing fits.</summary>
        public bool ApplyFootprint(Vector2 targetMeters)
        {
            bool selected = TrySelectFrame(targetMeters, out string frameId, out Vector2 frameScale);
            ApplyLayout(frameId, frameScale);
            return selected;
        }

        /// <summary>A frame's reserved footprint at frameScale, clamped to its [shrink, grow].</summary>
        public Vector2 GetFrameFootprintMeters(string frameId, Vector2 frameScale)
        {
            if (!IsBaseFrameId(frameId) && HasLayoutFrames)
            {
                AttractionLayoutFrame frame = FindFrame(frameId);
                if (FrameHasValidBake(frame))
                    return Vector2.Scale(frame.Bake.AuthoredFootprintMeters,
                        ClampScale(frameScale, frame.Bake.ShrinkScale, frame.Bake.GrowScale));
            }
            return GetBaseFootprintMeters(frameScale, false);
        }

        /// <summary>
        /// Editor debug preview. Zero target + empty id = unset (real park packing is
        /// untouched). Empty id = auto (TrySelectFrame); otherwise the forced frame at
        /// target / its authored size. Play Mode applies; Edit Mode is ghosts only.
        /// </summary>
        public void SetLayoutPreview(Vector2 targetMeters, string frameIdOrEmpty)
        {
            layoutPreviewFootprintMeters = new Vector2(
                Mathf.Max(0f, targetMeters.x), Mathf.Max(0f, targetMeters.y));
            layoutPreviewFrameId = frameIdOrEmpty ?? "";
#if UNITY_EDITOR
            if (Application.isPlaying) ApplyLayoutPreview();
#endif
        }

        /// <summary>The frame + frame scale the stored editor preview resolves to.</summary>
        public bool TryResolveLayoutPreview(out string frameId, out Vector2 frameScale)
        {
            Vector2 target = layoutPreviewFootprintMeters;
            bool hasTarget = target.x > 0f && target.y > 0f;
            if (!string.IsNullOrEmpty(layoutPreviewFrameId)
                && TryGetFrameEnvelope(layoutPreviewFrameId, out AttractionFrameEnvelope forced))
            {
                frameId = forced.frameId;
                frameScale = hasTarget
                    ? ClampScale(DivideFootprint(target, forced.authoredMeters), forced.shrinkScale, forced.growScale)
                    : Vector2.one;
                return true;
            }
            if (!hasTarget)
            {
                frameId = BaseFrameId;
                frameScale = Vector2.one;
                return true;
            }
            return TrySelectFrame(target, out frameId, out frameScale);
        }

        /// <summary>Editor/baker mutation API.</summary>
        public void SetLayoutFrames(List<AttractionLayoutFrame> frames)
        {
            layoutFrames = frames ?? new List<AttractionLayoutFrame>();
            warnedLayoutFrameIds = null;
        }

        /// <summary>Editor/baker mutation API.</summary>
        public void SetLayoutBaseStates(List<AttractionLayoutBaseState> states)
            => layoutBaseStates = states ?? new List<AttractionLayoutBaseState>();

#if UNITY_EDITOR
        private void ApplyLayoutPreview()
        {
            // A walls-only preview layers onto whatever the park applied; a size or
            // frame preview replaces the layout as before. Walls override only when set.
            bool layoutPreview = (layoutPreviewFootprintMeters.x > 0f && layoutPreviewFootprintMeters.y > 0f)
                || !string.IsNullOrEmpty(layoutPreviewFrameId);
            string frameId = CurrentFrameId;
            Vector2 frameScale = CurrentFrameScale;
            if (layoutPreview) TryResolveLayoutPreview(out frameId, out frameScale);
            currentWalls = layoutPreviewWalls != AttractionWallMask.None ? layoutPreviewWalls : parkWalls;
            ApplyLayoutInternal(frameId, frameScale, true);
        }
#endif

        private struct LayoutPose
        {
            public bool active;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;

            public static LayoutPose Capture(Transform target) => new LayoutPose
            {
                active = target.gameObject.activeSelf,
                position = target.localPosition,
                rotation = target.localRotation,
                scale = target.localScale,
            };
        }

        private bool ApplyLayoutInternal(
            string frameId,
            Vector2 frameScale,
            bool replaceGeneratedSurfacesImmediately)
        {
            if (!UsesLayoutPipeline)
            {
                // Legacy content: only Base exists. Anything else is a stale id.
                if (!IsBaseFrameId(frameId)) WarnUnknownFrameOnce(frameId);
                return ApplyPackingVariantInternal(frameScale, false, replaceGeneratedSurfacesImmediately);
            }

            AttractionLayoutFrame frame = null;
            if (!IsBaseFrameId(frameId))
            {
                frame = FindFrame(frameId);
                if (!FrameHasValidBake(frame))
                {
                    frame = null;
                    WarnUnknownFrameOnce(frameId);
                }
            }
            AttractionPackingBake bake = frame != null ? frame.Bake : HasPackingBake ? packingBake : null;
            Transform root = transform;

            // Resolve the complete target state first and write each transform
            // once, so re-applying (or switching frames) never flickers objects
            // through OnDisable/OnEnable.
            var desired = new Dictionary<Transform, LayoutPose>();
            var order = new List<Transform>();
            if (layoutBaseStates != null)
            {
                for (int i = 0; i < layoutBaseStates.Count; i++)
                {
                    AttractionLayoutBaseState state = layoutBaseStates[i];
                    Transform target = state?.Resolve(root);
                    if (target == null) continue;
                    if (!desired.ContainsKey(target)) order.Add(target);
                    desired[target] = new LayoutPose
                    {
                        active = state.active,
                        position = state.localPosition,
                        rotation = state.localRotation,
                        scale = state.localScale,
                    };
                }
            }
            if (frame?.overrides != null)
            {
                for (int i = 0; i < frame.overrides.Count; i++)
                {
                    AttractionLayoutOverride change = frame.overrides[i];
                    Transform target = change?.Resolve(root);
                    if (target == null) continue;
                    if (!desired.TryGetValue(target, out LayoutPose pose))
                    {
                        pose = LayoutPose.Capture(target);
                        order.Add(target);
                    }
                    if (change.setActive) pose.active = change.active;
                    if (change.setPosition) pose.position = change.localPosition;
                    if (change.setRotation) pose.rotation = change.localRotation;
                    if (change.setScale) pose.scale = change.localScale;
                    desired[target] = pose;
                }
            }

            Vector2 minimumScale = bake != null ? bake.ShrinkScale : Vector2.one;
            Vector2 maximumScale = bake != null ? bake.GrowScale : Vector2.one;
            Vector2 scale = ClampScale(frameScale, minimumScale, maximumScale);
            if (bake != null)
            {
                IReadOnlyList<AttractionPropPackingPose> poses = bake.Props;
                for (int i = 0; i < poses.Count; i++)
                {
                    AttractionPropPackingPose pose = poses[i];
                    Transform prop = pose?.Prop;
                    if (prop == null) continue;
                    if (!desired.TryGetValue(prop, out LayoutPose target))
                    {
                        target = LayoutPose.Capture(prop);
                        order.Add(prop);
                    }
                    // Same per-pose math as the legacy variant, never its essential branch.
                    target.position = ResolvePose(pose.AuthoredLocalPosition, pose.ShrunkLocalPosition,
                        pose.GrownLocalPosition, scale, minimumScale, maximumScale);
                    target.active = pose.AuthoredActive;
                    desired[prop] = target;
                }
            }

            // Wall layers go last so they win over the frame and its bake for
            // the objects they move. Footprint = what this call is applying.
            ResolveWallLayers(root, currentWalls,
                bake != null ? Vector2.Scale(bake.AuthoredFootprintMeters, scale) : AuthoredFootprintMeters,
                desired, order);

            IReadOnlyList<AttractionPackingFollowerPose> followers = bake != null
                ? bake.Followers
                : Array.Empty<AttractionPackingFollowerPose>();
            var followerTransforms = new HashSet<Transform>();
            var followerBefore = new Vector3[followers.Count * 2];
            for (int i = 0; i < followers.Count; i++)
            {
                Transform follower = followers[i]?.Follower;
                if (follower == null) continue;
                followerTransforms.Add(follower);
                followerBefore[i * 2] = follower.position;
                followerBefore[i * 2 + 1] = follower.localScale;
            }

            for (int i = 0; i < order.Count; i++)
            {
                Transform target = order[i];
                LayoutPose pose = desired[target];
                // Followers get their position and scale from the bake below.
                bool follower = followerTransforms.Contains(target);
                if (!follower && !target.localPosition.Equals(pose.position)) target.localPosition = pose.position;
                if (!target.localRotation.Equals(pose.rotation)) target.localRotation = pose.rotation;
                if (!follower && !target.localScale.Equals(pose.scale)) target.localScale = pose.scale;
            }
            for (int i = 0; i < order.Count; i++)
            {
                Transform target = order[i];
                bool active = desired[target].active;
                if (target.gameObject.activeSelf != active) target.gameObject.SetActive(active);
            }

            List<FloorAnchor> anchorsToRecache = null;
            for (int i = 0; i < followers.Count; i++)
            {
                AttractionPackingFollowerPose pose = followers[i];
                Transform follower = pose?.Follower;
                if (follower == null) continue;
                Vector3 worldPosition = root.TransformPoint(GetFollowerPosition(bake, pose, scale, false));
                Vector3 localScale = GetFollowerLocalScale(bake, pose, scale, false);
                if (follower.position != worldPosition) follower.position = worldPosition;
                if (!follower.localScale.Equals(localScale)) follower.localScale = localScale;
                bool changed = (followerBefore[i * 2] - worldPosition).sqrMagnitude > 0.0000001f
                    || (followerBefore[i * 2 + 1] - localScale).sqrMagnitude > 0.0000001f;
                if (!changed) continue;
                FloorAnchor anchor = follower.GetComponent<FloorAnchor>();
                if (anchor == null) continue;
                if (anchorsToRecache == null) anchorsToRecache = new List<FloorAnchor>();
                anchorsToRecache.Add(anchor);
            }

            if (bake == null)
            {
                // Base has no usable bake: the authored hierarchy is restored and
                // the runtime footprint falls back to the authored Size.
                bool wasPacked = hasRuntimePackingFootprint;
                hasRuntimePackingFootprint = false;
                runtimeFootprintFromFrame = false;
                SetCurrentLayout(BaseFrameId, Vector2.one);
                RefreshPackingDependentSystems(wasPacked, replaceGeneratedSurfacesImmediately);
                NotifyLayoutApplied();
                NotifyWallsApplied();
                return false;
            }

            Vector2 nextFootprint = Vector2.Scale(bake.AuthoredFootprintMeters, scale);
            bool footprintChanged = !hasRuntimePackingFootprint
                || (runtimePackingFootprintMeters - nextFootprint).sqrMagnitude > 0.000001f;
            runtimePackingFootprintMeters = nextFootprint;
            hasRuntimePackingFootprint = true;
            runtimeFootprintFromFrame = frame != null;
            SetCurrentLayout(frame != null ? FrameIdOf(frame) : BaseFrameId, scale);
            RefreshPackingDependentSystems(footprintChanged, replaceGeneratedSurfacesImmediately);
            if (anchorsToRecache != null)
                for (int i = 0; i < anchorsToRecache.Count; i++)
                    anchorsToRecache[i].RecacheCorners();
            NotifyLayoutApplied();
            NotifyWallsApplied();
            return true;
        }

        /// <summary>
        /// Legacy (packingScale relative to Base, essentialOnly) → frame. With a valid
        /// Minimum frame the essential variant IS that frame at the same footprint.
        /// </summary>
        private void MapLegacyVariant(Vector2 packingScale, bool essentialOnly,
            out string frameId, out Vector2 frameScale)
        {
            AttractionLayoutFrame minimum = essentialOnly ? MinimumFrame : null;
            if (minimum != null && FrameHasValidBake(minimum))
            {
                Vector2 baseAuthored = HasPackingBake
                    ? packingBake.AuthoredFootprintMeters
                    : AuthoredFootprintMeters;
                AttractionPackingBake bake = minimum.Bake;
                Vector2 target = Vector2.Scale(baseAuthored, packingScale);
                frameId = MinimumFrameId;
                frameScale = ClampScale(DivideFootprint(target, bake.AuthoredFootprintMeters),
                    bake.ShrinkScale, bake.GrowScale);
                return;
            }
            frameId = BaseFrameId;
            frameScale = packingScale;
        }

        private bool IsLadderFrame(AttractionLayoutFrame frame)
        {
            if (frame == null) return false;
            if (frame.kind == AttractionLayoutFrameKind.Custom
                && (!IsValidFrameId(frame.id) || frame.id == BaseFrameId || frame.id == MinimumFrameId))
                return false;
            return FrameHasValidBake(frame);
        }

        private static string FrameIdOf(AttractionLayoutFrame frame) =>
            frame.kind == AttractionLayoutFrameKind.Minimum ? MinimumFrameId : frame.id;

        private static bool IsBaseFrameId(string frameId) =>
            string.IsNullOrEmpty(frameId) || frameId == BaseFrameId;

        private static int KindRank(string kind) =>
            kind == "base" ? 0 : kind == "custom" ? 1 : 2;

        private static AttractionFrameEnvelope EnvelopeFor(string frameId, string kind,
            string frameName, AttractionPackingBake bake) => new AttractionFrameEnvelope
        {
            frameId = frameId,
            kind = kind,
            name = frameName,
            authoredMeters = bake.AuthoredFootprintMeters,
            shrinkMeters = bake.ShrinkFootprintMeters,
            growMeters = bake.GrowFootprintMeters,
            shrinkScale = bake.ShrinkScale,
            growScale = bake.GrowScale,
        };

        private static int SmallestShrinkIndex(List<AttractionFrameEnvelope> ladder)
        {
            int smallest = 0;
            for (int i = 1; i < ladder.Count; i++)
                if (Area(ladder[i].shrinkMeters) < Area(ladder[smallest].shrinkMeters) - 0.0001f)
                    smallest = i;
            return smallest;
        }

        /// <summary>
        /// Fired after every layout apply that changes (or first sets) the applied
        /// frame, its scale or the runtime footprint. Lua: onlayout(frame_id, scale,
        /// footprint) in a LuaBehaviour under this attraction (sticky: fired once
        /// after start() with the current layout).
        /// </summary>
        public event Action<AttractionTemplate> LayoutApplied;

        private bool layoutNotifiedOnce;
        private string lastNotifiedFrameId;
        private Vector2 lastNotifiedFrameScale;
        private Vector2 lastNotifiedFootprint;

        private void NotifyLayoutApplied()
        {
            // With no park data the rect IS the footprint (all unknown), so a layout
            // change is a rect change too (LayoutLab finding 3): on_rect_changed
            // fires, deduped by signature like every other RectChanged.
            if (parkRectOpenings == null) NotifyRectChanged();
            string frameId = CurrentFrameId;
            Vector2 scale = CurrentFrameScale;
            Vector2 footprint = RuntimeFootprintMeters;
            if (layoutNotifiedOnce && lastNotifiedFrameId == frameId
                && (lastNotifiedFrameScale - scale).sqrMagnitude <= 0.000001f
                && (lastNotifiedFootprint - footprint).sqrMagnitude <= 0.000001f) return;
            layoutNotifiedOnce = true;
            lastNotifiedFrameId = frameId;
            lastNotifiedFrameScale = scale;
            lastNotifiedFootprint = footprint;
            LayoutApplied?.Invoke(this);
        }

        private void SetCurrentLayout(string frameId, Vector2 frameScale)
        {
            hasCurrentLayout = true;
            currentFrameId = frameId;
            currentFrameScale = frameScale;
        }

        private void WarnUnknownFrameOnce(string frameId)
        {
            if (warnedLayoutFrameIds == null) warnedLayoutFrameIds = new HashSet<string>();
            if (!warnedLayoutFrameIds.Add(frameId ?? "")) return;
            Debug.LogWarning($"[AttractionPacking] '{name}' has no baked layout frame '{frameId}'. Using Base.");
        }

        private static float Area(Vector2 footprint) => footprint.x * footprint.y;

        private static Vector2 ClampScale(Vector2 scale, Vector2 minimum, Vector2 maximum) =>
            new Vector2(
                Mathf.Clamp(scale.x, minimum.x, maximum.x),
                Mathf.Clamp(scale.y, minimum.y, maximum.y));

        private static Vector2 DivideFootprint(Vector2 value, Vector2 authored) =>
            new Vector2(
                authored.x > 0f ? value.x / authored.x : 1f,
                authored.y > 0f ? value.y / authored.y : 1f);

        public void SetShowPackingGizmos(bool value) => showPackingGizmos = value;

        // ---------------------------------------------------------------------
        // Entries & exits. Base owns a list; each layout frame either follows it
        // (default) or carries its own. Positions are 0-1 perimeter coordinates,
        // so they stretch with whatever footprint the layout ends up at.
        // ---------------------------------------------------------------------

        /// <summary>Base entries/exits (as authored).</summary>
        public IReadOnlyList<AttractionPortal> Portals =>
            portals ?? (IReadOnlyList<AttractionPortal>)Array.Empty<AttractionPortal>();

        /// <summary>
        /// The effective entries/exits of one layout frame ("base"/empty = Base).
        /// A frame that does not override portals, and an unknown id, use Base.
        /// </summary>
        public IReadOnlyList<AttractionPortal> GetPortals(string frameId)
        {
            AttractionLayoutFrame frame = IsBaseFrameId(frameId) ? null : FindFrame(frameId);
            if (frame != null && frame.overridePortals)
                return frame.portals ?? (IReadOnlyList<AttractionPortal>)Array.Empty<AttractionPortal>();
            return Portals;
        }

        /// <summary>Runtime: the entries/exits of the layout frame last applied.</summary>
        public IReadOnlyList<AttractionPortal> CurrentPortals => GetPortals(CurrentFrameId);

        /// <summary>True when the frame (or Base) has at least one entry.</summary>
        public bool HasEntry(string frameId = BaseFrameId)
        {
            IReadOnlyList<AttractionPortal> list = GetPortals(frameId);
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].IsEntry) return true;
            return false;
        }

        /// <summary>
        /// A portal's world position (on the floor plane of the attraction root)
        /// and the world direction a guest walks through it, at the current
        /// runtime footprint.
        /// </summary>
        public void GetPortalWorldPose(AttractionPortal portal, out Vector3 position, out Vector3 walkDirection)
            => GetPortalWorldPose(portal, RuntimeFootprintMeters, out position, out walkDirection);

        /// <summary>As above, on an explicit prefab-local footprint (metres, X × Z).</summary>
        public void GetPortalWorldPose(AttractionPortal portal, Vector2 footprintMeters,
            out Vector3 position, out Vector3 walkDirection)
        {
            float t = portal != null ? portal.position : AttractionPortalGeometry.DefaultEntryPosition;
            position = transform.TransformPoint(AttractionPortalGeometry.LocalPoint(t, footprintMeters));
            walkDirection = transform.TransformDirection(AttractionPortalGeometry.WalkDirection(portal));
        }

        /// <summary>Editor/batch-authoring API: replace the Base list.</summary>
        public void SetPortals(List<AttractionPortal> value) =>
            portals = value ?? new List<AttractionPortal>();

        /// <summary>Editor/batch-authoring hook used to replace the serialized bake atomically.</summary>
        public void SetPackingBake(AttractionPackingBake value) => packingBake = value;

        // ---------------------------------------------------------------------
        // Wall layers (WALL_LAYERS_SPEC.md). "No walls" is the baseline and must
        // always work. Core seats the attraction on a rect whose edges it has
        // classified; every side of the attraction standing on a wall edge is
        // walled, and the wall layer for that side switches on. Nothing measures
        // walls here: the rect edge IS the wall.
        // ---------------------------------------------------------------------

        /// <summary>Wall layers in priority order (first wins a shared object).</summary>
        public IReadOnlyList<AttractionWallLayer> WallLayers =>
            wallLayers ?? (IReadOnlyList<AttractionWallLayer>)Array.Empty<AttractionWallLayer>();

        /// <summary>True when at least one wall layer exists.</summary>
        public bool HasWallLayers
        {
            get
            {
                if (wallLayers == null) return false;
                for (int i = 0; i < wallLayers.Count; i++)
                    if (wallLayers[i] != null) return true;
                return false;
            }
        }

        /// <summary>The sides this attraction has extras for, in priority order.</summary>
        public List<AttractionWallSide> WallLayerSides
        {
            get
            {
                var sides = new List<AttractionWallSide>(4);
                if (wallLayers == null) return sides;
                for (int i = 0; i < wallLayers.Count; i++)
                    if (wallLayers[i] != null && !sides.Contains(wallLayers[i].side)) sides.Add(wallLayers[i].side);
                return sides;
            }
        }

        /// <summary>The wall layer for a side, or null.</summary>
        public AttractionWallLayer FindWallLayer(AttractionWallSide side)
        {
            if (wallLayers == null) return null;
            for (int i = 0; i < wallLayers.Count; i++)
                if (wallLayers[i] != null && wallLayers[i].side == side) return wallLayers[i];
            return null;
        }

        /// <summary>A wall layer by id, or null.</summary>
        public AttractionWallLayer FindWallLayerById(string id)
        {
            if (string.IsNullOrEmpty(id) || wallLayers == null) return null;
            for (int i = 0; i < wallLayers.Count; i++)
                if (wallLayers[i] != null && wallLayers[i].id == id) return wallLayers[i];
            return null;
        }

        /// <summary>Runtime: which sides are on a real wall (None until core says otherwise).</summary>
        public AttractionWallMask Walls => currentWalls;

        /// <summary>Runtime: Walls as "front,left" ("" when none).</summary>
        public string WallsWire => AttractionWallSides.ToWire(currentWalls);

        /// <summary>Runtime: is this side on a real wall? Bonus content only — never gate gameplay on it.</summary>
        public bool HasWall(AttractionWallSide side) => AttractionWallSides.Has(currentWalls, side);

        /// <summary>Lua-friendly HasWall("front" | "back" | "right" | "left").</summary>
        public bool HasWallNamed(string side) =>
            AttractionWallSides.TryParse(side, out AttractionWallSide parsed) && HasWall(parsed);

        /// <summary>Fired after every layout apply that changes (or first sets) the walled sides.</summary>
        public event Action<AttractionTemplate> WallsApplied;

        /// <summary>ApplyLayout with the walled sides core resolved for this seat.</summary>
        public bool ApplyLayout(string frameId, Vector2 frameScale, AttractionWallMask walls)
        {
            currentWalls = walls;
            parkWalls = walls;
            return ApplyLayoutInternal(frameId, frameScale, false);
        }

        /// <summary>
        /// Change the walled sides and re-apply the current layout (frame and scale
        /// unchanged). Attractions without wall layers only record the value.
        /// </summary>
        public void SetWalls(AttractionWallMask walls)
        {
            if (!HasWallLayers)
            {
                currentWalls = walls;
                parkWalls = walls;
                NotifyWallsApplied();
                return;
            }
            ApplyLayout(CurrentFrameId, CurrentFrameScale, walls);
        }

        /// <summary>Editor debug preview of walled sides; Play Mode applies it.</summary>
        public void SetLayoutPreviewWalls(AttractionWallMask walls)
        {
            layoutPreviewWalls = walls;
#if UNITY_EDITOR
            if (Application.isPlaying) ApplyLayoutPreview();
#endif
        }

        /// <summary>Editor/baker mutation API.</summary>
        public void SetWallLayers(List<AttractionWallLayer> layers) =>
            wallLayers = layers ?? new List<AttractionWallLayer>();

        // ---------------------------------------------------------------------
        // Rect edges (RECT_EDGES_SPEC.md, v3). The rect this attraction is
        // seated on, per side, as spans of wall / opening / unknown, in this
        // attraction's own frame. Core sets it at seat time and whenever the map
        // changes; with no park data every side is unknown (the Editor can author
        // or inject test data).
        // Read-only for content. Kept out of the XLua wrapper ([BlackList]):
        // Lua reads it through dp.rect_openings() / on_rect_changed.
        // ---------------------------------------------------------------------

        // Editor test data for the Park Simulator / Play Mode (never used on device).
        [SerializeField, HideInInspector]
        private bool useRectOpeningsPreview;

        [SerializeField, HideInInspector]
        private AttractionRectOpenings rectOpeningsPreview;

        [NonSerialized] private AttractionRectOpenings parkRectOpenings;
        [NonSerialized] private string lastRectSignature;

        /// <summary>
        /// The seated rect's openings in this attraction's frame. Never null:
        /// core's data, else (Editor only) the authored preview, else every side
        /// one unknown span around the current footprint.
        /// </summary>
        [XLua.BlackList]
        public AttractionRectOpenings RectOpenings
        {
            get
            {
                if (parkRectOpenings != null) return parkRectOpenings;
#if UNITY_EDITOR
                if (useRectOpeningsPreview && rectOpeningsPreview != null)
                {
                    var preview = rectOpeningsPreview.Clone();
                    preview.hasData = true;
                    if (preview.rectSize.x <= 0f || preview.rectSize.y <= 0f) preview.rectSize = RuntimeFootprintMeters;
                    return preview;
                }
#endif
                return AttractionRectOpenings.AllOpen(RuntimeFootprintMeters);
            }
        }

        /// <summary>True once core has handed this attraction its rect's openings.</summary>
        [XLua.BlackList]
        public bool HasParkRectOpenings => parkRectOpenings != null;

        /// <summary>
        /// Where <see cref="RectOpenings"/> comes from: "park" (core's seated
        /// rect), "preview" (the Editor's authored preview) or "default" (every
        /// side open around the current footprint). Same order as the getter.
        /// </summary>
        [XLua.BlackList]
        public string RectOpeningsSource
        {
            get
            {
                if (parkRectOpenings != null) return "park";
#if UNITY_EDITOR
                if (useRectOpeningsPreview && rectOpeningsPreview != null) return "preview";
#endif
                return "default";
            }
        }

        /// <summary>
        /// Fired when <see cref="RectOpenings"/> changes: the attraction is seated
        /// or re-seated, the space map rebuilds (scan growth, a drawn rect, a
        /// title switch) or the rect's borders change. Not fired when nothing a
        /// reader can see changed.
        /// </summary>
        [XLua.BlackList]
        public event Action<AttractionTemplate> RectChanged;

        /// <summary>Core (or an Editor tool / Park Simulator): the seated rect's sides as v3 spans
        /// (null = no rect; reverts to all unknown).</summary>
        [XLua.BlackList]
        public void SetRectOpenings(AttractionRectOpenings value)
        {
            parkRectOpenings = value?.Clone();
            if (parkRectOpenings != null) parkRectOpenings.hasData = true;
            NotifyRectChanged();
        }

        /// <summary>Editor/test API: the authored preview data and whether it is used.</summary>
        [XLua.BlackList]
        public AttractionRectOpenings RectOpeningsPreview => rectOpeningsPreview;

        [XLua.BlackList]
        public bool UseRectOpeningsPreview => useRectOpeningsPreview;

        [XLua.BlackList]
        public void SetRectOpeningsPreview(AttractionRectOpenings value, bool use)
        {
            rectOpeningsPreview = value;
            useRectOpeningsPreview = use;
            NotifyRectChanged();
        }

        private void NotifyRectChanged()
        {
            string sig = RectOpenings.Signature();
            if (sig == lastRectSignature) return;
            lastRectSignature = sig;
            RectChanged?.Invoke(this);
        }

        private bool UsesLayoutPipeline => HasLayoutFrames || HasWallLayers;

        private AttractionWallMask lastNotifiedWalls;

        private void NotifyWallsApplied()
        {
            if (wallsAppliedOnce && lastNotifiedWalls == currentWalls) return;
            wallsAppliedOnce = true;
            lastNotifiedWalls = currentWalls;
            WallsApplied?.Invoke(this);
        }

        /// <summary>
        /// Layer the walled sides' overrides onto the resolved layout. Each object
        /// takes the FIRST walled layer in list order that moves it; objects no
        /// walled layer moves keep the frame/Base pose already in `desired`.
        /// </summary>
        private void ResolveWallLayers(Transform root, AttractionWallMask walls, Vector2 footprintMeters,
            Dictionary<Transform, LayoutPose> desired, List<Transform> order)
        {
            if (walls == AttractionWallMask.None || wallLayers == null) return;
            var claimed = new HashSet<Transform>();
            var mine = new List<Transform>();
            for (int l = 0; l < wallLayers.Count; l++)
            {
                AttractionWallLayer layer = wallLayers[l];
                if (layer?.overrides == null || !AttractionWallSides.Has(walls, layer.side)) continue;
                mine.Clear();
                for (int i = 0; i < layer.overrides.Count; i++)
                {
                    AttractionLayoutOverride change = layer.overrides[i];
                    Transform target = change?.Resolve(root);
                    if (target == null || claimed.Contains(target)) continue;
                    if (!desired.TryGetValue(target, out LayoutPose pose))
                    {
                        pose = LayoutPose.Capture(target);
                        order.Add(target);
                    }
                    if (change.setActive) pose.active = change.active;
                    if (change.setPosition)
                        pose.position = target.parent == root
                            ? AnchorToSide(change.localPosition, layer, footprintMeters)
                            : change.localPosition;
                    if (change.setRotation) pose.rotation = change.localRotation;
                    if (change.setScale) pose.scale = change.localScale;
                    desired[target] = pose;
                    mine.Add(target);
                }
                for (int i = 0; i < mine.Count; i++) claimed.Add(mine[i]);
            }
        }

        /// <summary>
        /// A root-level position authored against the layer's Base footprint, moved
        /// to the applied footprint: same distance from this side's edge (the
        /// wall), same relative place along it. Height is never changed. Nested
        /// objects ride along with their parent instead.
        /// </summary>
        public static Vector3 AnchorToSide(Vector3 authored, AttractionWallLayer layer, Vector2 footprintMeters)
        {
            Vector2 from = layer != null ? layer.authoredFootprintMeters : Vector2.zero;
            if (from.x <= 0f || from.y <= 0f || footprintMeters.x <= 0f || footprintMeters.y <= 0f)
                return authored;
            Vector3 p = authored;
            switch (layer.side)
            {
                case AttractionWallSide.Front:
                    p.z += (footprintMeters.y - from.y) * 0.5f;
                    p.x *= footprintMeters.x / from.x;
                    break;
                case AttractionWallSide.Back:
                    p.z -= (footprintMeters.y - from.y) * 0.5f;
                    p.x *= footprintMeters.x / from.x;
                    break;
                case AttractionWallSide.Right:
                    p.x += (footprintMeters.x - from.x) * 0.5f;
                    p.z *= footprintMeters.y / from.y;
                    break;
                case AttractionWallSide.Left:
                    p.x -= (footprintMeters.x - from.x) * 0.5f;
                    p.z *= footprintMeters.y / from.y;
                    break;
            }
            return p;
        }
    }
}
