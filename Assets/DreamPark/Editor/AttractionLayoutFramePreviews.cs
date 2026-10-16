using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Shared layout-frame preview bookkeeping for the preview generator
    /// (ContentProcessor), the Preview Editor and the Content Uploader: which
    /// frames of an attraction get their own preview PNG, the order frames are
    /// shown in, and which frame PNGs already exist on disk. Paths themselves
    /// come from <see cref="AttractionLayoutFramePreviewPaths"/>.
    /// </summary>
    public static class AttractionLayoutFramePreviews
    {
        private const float MetersPerFoot = 0.3048f;

        public struct FrameInfo
        {
            public string frameId;   // "base" | custom id | "minimum"
            public string name;
            public string kind;      // "base" | "custom" | "minimum"
            public Vector2 sizeFeet; // authored width (local X) × length (local Z)

            public bool IsBase => frameId == AttractionTemplate.BaseFrameId;
        }

        /// <summary>
        /// The id a frame's preview is stored under, or null when the frame cannot
        /// have one. Base is implicit (Previews/{prefab}.png) and never listed here;
        /// a Custom frame needs a valid, non-reserved id.
        /// </summary>
        public static string PreviewFrameId(AttractionLayoutFrame frame)
        {
            if (frame == null) return null;
            if (frame.kind == AttractionLayoutFrameKind.Minimum) return AttractionTemplate.MinimumFrameId;
            string id = frame.id;
            if (!AttractionTemplate.IsValidFrameId(id)
                || id == AttractionTemplate.BaseFrameId
                || id == AttractionTemplate.MinimumFrameId) return null;
            return id;
        }

        /// <summary>
        /// Every frame in card order: Base first, then Custom frames by authored
        /// area (largest first, list order on ties), Minimum last. Base only when
        /// the attraction has no frames; empty for null. Frames without a
        /// previewable id are skipped, as are duplicate ids (first wins).
        /// </summary>
        public static List<FrameInfo> DisplayFrames(AttractionTemplate attraction)
        {
            var result = new List<FrameInfo>();
            if (attraction == null) return result;

            Vector3 size = attraction.Size;
            result.Add(new FrameInfo
            {
                frameId = AttractionTemplate.BaseFrameId,
                name = "Base",
                kind = "base",
                sizeFeet = new Vector2(size.x, size.z) / MetersPerFoot,
            });

            var custom = new List<KeyValuePair<FrameInfo, int>>();
            bool hasMinimum = false;
            FrameInfo minimum = default;
            var seen = new HashSet<string>(StringComparer.Ordinal) { AttractionTemplate.BaseFrameId };
            IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
            for (int i = 0; i < frames.Count; i++)
            {
                AttractionLayoutFrame frame = frames[i];
                string id = PreviewFrameId(frame);
                if (id == null || !seen.Add(id)) continue;
                bool isMinimum = frame.kind == AttractionLayoutFrameKind.Minimum;
                var info = new FrameInfo
                {
                    frameId = id,
                    name = string.IsNullOrWhiteSpace(frame.name) ? (isMinimum ? "Minimum" : id) : frame.name,
                    kind = isMinimum ? "minimum" : "custom",
                    sizeFeet = frame.sizeFeet,
                };
                if (isMinimum)
                {
                    hasMinimum = true;
                    minimum = info;
                }
                else custom.Add(new KeyValuePair<FrameInfo, int>(info, i));
            }

            custom.Sort((a, b) =>
            {
                float areaA = a.Key.sizeFeet.x * a.Key.sizeFeet.y;
                float areaB = b.Key.sizeFeet.x * b.Key.sizeFeet.y;
                if (Mathf.Abs(areaA - areaB) > 0.0001f) return areaB.CompareTo(areaA);
                return a.Value.CompareTo(b.Value);
            });
            for (int i = 0; i < custom.Count; i++) result.Add(custom[i].Key);
            if (hasMinimum) result.Add(minimum);
            return result;
        }

        /// <summary>Ids of every frame that gets its own preview PNG (never Base), in card order.</summary>
        public static List<string> PreviewFrameIds(AttractionTemplate attraction)
        {
            var ids = new List<string>();
            List<FrameInfo> frames = DisplayFrames(attraction);
            for (int i = 0; i < frames.Count; i++)
                if (!frames[i].IsBase) ids.Add(frames[i].frameId);
            return ids;
        }

        /// <summary>
        /// Every parseable frame preview PNG in a content folder's Previews/Frames/.
        /// Read through System.IO so it also sees files written mid-import.
        /// </summary>
        public static List<(string path, string prefabName, string frameId)> ExistingPreviews(string contentId)
        {
            var result = new List<(string, string, string)>();
            if (string.IsNullOrEmpty(contentId)) return result;
            string folder = AttractionLayoutFramePreviewPaths.FolderFor(contentId);
            if (!Directory.Exists(folder)) return result;
            string[] files;
            try { files = Directory.GetFiles(folder, "*" + AttractionLayoutFramePreviewPaths.Extension); }
            catch (Exception) { return result; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                if (!AttractionLayoutFramePreviewPaths.TryParse(file, out string prefab, out string frame)) continue;
                result.Add((AttractionLayoutFramePreviewPaths.PathFor(contentId, prefab, frame), prefab, frame));
            }
            return result;
        }

        /// <summary>Frame preview PNGs on disk for one prefab: frameId → asset path.</summary>
        public static Dictionary<string, string> ExistingPreviewPaths(string contentId, string prefabName)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var preview in ExistingPreviews(contentId))
                if (string.Equals(preview.prefabName, prefabName, StringComparison.Ordinal))
                    result[preview.frameId] = preview.path;
            return result;
        }

        /// <summary>"W × L ft", e.g. "20 × 12.5 ft".</summary>
        public static string FormatFeet(Vector2 sizeFeet) =>
            Feet(sizeFeet.x) + " × " + Feet(sizeFeet.y) + " ft";

        private static string Feet(float value) =>
            (Mathf.Round(value * 10f) / 10f).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
