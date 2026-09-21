using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    [Serializable]
    public sealed class DreamSequenceLevel
    {
        [Tooltip("Stable source prefab GUID used by the Content Uploader.")]
        public string sourceGuid;

        [Tooltip("Addressable address loaded by the generated sequence runtime.")]
        public string address;

        public string displayName;
    }

    /// <summary>
    /// A generated, single-room fallback that can play an ordered list of attractions.
    /// The room is deliberately allowed to exceed an attraction's authored grow limit:
    /// sequence presentation must occupy one consistent physical footprint.
    /// </summary>
    public sealed class DreamSequenceTemplate : AttractionTemplate
    {
        public const float StandardWidthFeet = 12f;
        public const float StandardLengthFeet = 18f;

        [Header("Dream Sequence")]
        public string sequenceName = "Dream Sequence";
        public List<DreamSequenceLevel> levels = new List<DreamSequenceLevel>();
        [HideInInspector] public bool generatedByContentUploader = true;
    }

    public static class DreamSequenceCompatibility
    {
        private const float FeetPerMeter = 3.280839895f;

        public static bool IsCompatible(AttractionTemplate attraction)
        {
            if (attraction == null || attraction is DreamSequenceTemplate) return false;

            Vector2 authored = attraction.DimensionsInFeet;
            if (FitsWithRotation(authored, DreamSequenceTemplate.StandardWidthFeet,
                DreamSequenceTemplate.StandardLengthFeet)) return true;

            if (!attraction.HasPackingBake) return false;
            Vector2 shrinkFeet = attraction.PackingBake.ShrinkFootprintMeters * FeetPerMeter;
            return FitsWithRotation(shrinkFeet, DreamSequenceTemplate.StandardWidthFeet,
                DreamSequenceTemplate.StandardLengthFeet);
        }

        public static Vector2 SequenceScale(AttractionTemplate attraction)
        {
            if (attraction == null) return Vector2.one;
            Vector2 feet = attraction.DimensionsInFeet;
            if (feet.x <= 0f || feet.y <= 0f) return Vector2.one;

            // Do not clamp to PackingBake.GrowScale. Consistent sequence dimensions
            // intentionally override the attraction's normal park-placement ceiling.
            return ShouldRotate(attraction)
                ? new Vector2(
                    DreamSequenceTemplate.StandardLengthFeet / feet.x,
                    DreamSequenceTemplate.StandardWidthFeet / feet.y)
                : new Vector2(
                    DreamSequenceTemplate.StandardWidthFeet / feet.x,
                    DreamSequenceTemplate.StandardLengthFeet / feet.y);
        }

        public static bool ShouldRotate(AttractionTemplate attraction)
        {
            if (attraction == null) return false;
            Vector2 candidate = attraction.DimensionsInFeet;
            float width = DreamSequenceTemplate.StandardWidthFeet;
            float length = DreamSequenceTemplate.StandardLengthFeet;
            if (Fits(candidate, width, length)) return false;
            if (Fits(new Vector2(candidate.y, candidate.x), width, length)) return true;

            if (attraction.HasPackingBake)
            {
                candidate = attraction.PackingBake.ShrinkFootprintMeters * FeetPerMeter;
                if (Fits(candidate, width, length)) return false;
                if (Fits(new Vector2(candidate.y, candidate.x), width, length)) return true;
            }
            return false;
        }

        private static bool FitsWithRotation(Vector2 size, float width, float length)
        {
            return Fits(size, width, length) || Fits(new Vector2(size.y, size.x), width, length);
        }

        private static bool Fits(Vector2 size, float width, float length)
        {
            const float epsilon = 0.001f;
            return size.x <= width + epsilon && size.y <= length + epsilon;
        }
    }
}
