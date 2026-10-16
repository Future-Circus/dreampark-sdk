using UnityEngine;

namespace DreamPark
{
    public enum AttractionPackingScaleMode
    {
        XYZUniform,
        XZUniform,
        XZIndependent,
    }

    /// <summary>
    /// Opts a visual or floor-anchored object into an AttractionTemplate's baked
    /// packing poses without letting its footprint set the attraction's minimum.
    /// Keep this on the object whose transform should move and scale.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AttractionPackingFollower : MonoBehaviour
    {
        [Tooltip("Follow the attraction's baked shrink and growth variants. When off, this object participates in packing normally.")]
        public bool followPacking = true;

        [Tooltip("XYZ scales all axes equally; XZ Uniform preserves height; XZ Independent follows each floor axis separately.")]
        public AttractionPackingScaleMode scaleMode = AttractionPackingScaleMode.XZIndependent;

        [Tooltip("Avoid new overlaps with regular props at the baked shrink endpoints. Turn off for borders, floors, and other intentional overlays.")]
        public bool avoidNewPropOverlaps = true;
    }
}
