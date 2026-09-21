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

    /// <summary>
    /// Baked footprint ladder for one attraction. All dimensions are local-X/local-Z
    /// metres and remain rectangles so the existing room packer can consume them.
    /// </summary>
    [Serializable]
    public sealed class AttractionPackingBake
    {
        public const int CurrentVersion = 5;

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

        public int Version => version;
        public bool IsValid => version == CurrentVersion && authoredFootprintMeters.x > 0f && authoredFootprintMeters.y > 0f;
        public Vector2 AuthoredFootprintMeters => authoredFootprintMeters;
        public Vector2 SafeFootprintMeters => safeFootprintMeters;
        public Vector2 ShrinkFootprintMeters => shrinkFootprintMeters;
        public Vector2 GrowFootprintMeters => growFootprintMeters;
        public Vector2 EssentialShrinkFootprintMeters => essentialShrinkFootprintMeters;
        public Vector2 ShrinkScale => shrinkScale;
        public Vector2 GrowScale => growScale;
        public Vector2 EssentialShrinkScale => essentialShrinkScale;
        public IReadOnlyList<AttractionPropPackingPose> Props => props;
        public bool HasEssentialProps
        {
            get
            {
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
            List<AttractionPropPackingPose> props)
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
        }

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

        [Tooltip("Child prop roots that the attraction cannot function without. References may point anywhere inside a nested PropTemplate; the baker resolves the owning prop root.")]
        public List<Transform> essentialProps = new List<Transform>();

        [SerializeField, HideInInspector]
        public Vector2 maxGrowthScale = new Vector2(1.25f, 1.25f);

        [SerializeField, HideInInspector, Min(0f)]
        [Tooltip("Minimum edge-to-edge clearance preserved while props are pulled inward, in metres.")]
        public float shrinkClearanceMeters = 0.08f;

        [SerializeField, HideInInspector, Min(0f)]
        [Tooltip("Props this close together are treated as one group during growth.")]
        public float growGroupGapMeters = 0.35f;

        [SerializeField, HideInInspector, Min(0f)]
        [Tooltip("Nearby props whose centres align within this distance are kept in the same growth group.")]
        public float growAlignmentToleranceMeters = 0.12f;

        [SerializeField, HideInInspector]
        private AttractionPackingBake packingBake;

        [SerializeField, HideInInspector]
        private Vector2 packingPreviewScale = Vector2.one;

        [SerializeField, HideInInspector]
        private bool packingPreviewEssentialOnly;

        [SerializeField, HideInInspector]
        private bool showPackingGizmos = true;

        [NonSerialized] private bool hasRuntimePackingFootprint;
        [NonSerialized] private Vector2 runtimePackingFootprintMeters;

        public AttractionPackingBake PackingBake => packingBake;
        public bool HasPackingBake => packingBake != null && packingBake.IsValid;
        public Vector2 PackingPreviewScale => packingPreviewScale;
        public bool PackingPreviewEssentialOnly => packingPreviewEssentialOnly;
        public bool ShowPackingGizmos => showPackingGizmos;
        public override Vector2 RuntimeFootprintMeters => hasRuntimePackingFootprint
            ? runtimePackingFootprintMeters
            : base.RuntimeFootprintMeters;

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
        {
            if (!HasPackingBake) return false;

            bool useEssential = essentialOnly && packingBake.HasEssentialProps;
            Vector2 nextFootprint = GetPackingFootprintMeters(packingScale, useEssential);
            bool footprintChanged = !hasRuntimePackingFootprint
                || (runtimePackingFootprintMeters - nextFootprint).sqrMagnitude > 0.000001f;
            runtimePackingFootprintMeters = nextFootprint;
            hasRuntimePackingFootprint = true;
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
            RefreshPackingDependentSystems(footprintChanged);
            return true;
        }

        private void RefreshPackingDependentSystems(bool footprintChanged)
        {
            GetComponent<GameArea>()?.ComputeBounds();
            GetComponent<MusicArea>()?.ComputeBounds();

            // The shipping loader applies packing before Start, so its first floor
            // is already the right size. This branch covers editor slider changes
            // and fallback/legacy callers that select a variant after startup.
            if (!footprintChanged || !Application.isPlaying) return;
            if (runtimePlane != null) RegenerateFloor();
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
                ApplyPackingVariant(packingPreviewScale, packingPreviewEssentialOnly);
#endif
        }

#if UNITY_EDITOR
        private void Awake()
        {
            if (!Application.isPlaying || !HasPackingBake) return;
            if (packingPreviewScale == Vector2.one && !packingPreviewEssentialOnly) return;
            ApplyPackingVariant(packingPreviewScale, packingPreviewEssentialOnly);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || !HasPackingBake) return;
            if (packingPreviewScale == Vector2.one && !packingPreviewEssentialOnly) return;

            // Run after the park loader's Update. A non-default editor preview is
            // an explicit local override; the default (1,1) leaves real park
            // packingScale/essentialOnly decisions completely untouched.
            ApplyPackingVariant(packingPreviewScale, packingPreviewEssentialOnly);
        }
#endif

        public void SetShowPackingGizmos(bool value) => showPackingGizmos = value;

        /// <summary>Editor/batch-authoring hook used to replace the serialized bake atomically.</summary>
        public void SetPackingBake(AttractionPackingBake value) => packingBake = value;
    }
}
