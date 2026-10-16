using System;
using System.IO;

namespace DreamPark
{
    /// <summary>
    /// Where per-frame attraction previews live. Base keeps
    /// Previews/{prefabName}.png; every Custom/Minimum frame gets
    /// Previews/Frames/{prefabName}@{frameId}.png so frame art can never be
    /// mistaken for a base preview (or collide with its Addressables address).
    /// </summary>
    public static class AttractionLayoutFramePreviewPaths
    {
        public const string FolderName = "Frames";
        public const char Separator = '@';
        public const string Extension = ".png";

        /// <summary>Assets/Content/{contentId}/Previews/Frames</summary>
        public static string FolderFor(string contentId) =>
            $"Assets/Content/{contentId}/Previews/{FolderName}";

        /// <summary>Assets/Content/{contentId}/Previews/Frames/{prefabName}@{frameId}.png</summary>
        public static string PathFor(string contentId, string prefabName, string frameId) =>
            $"{FolderFor(contentId)}/{MetadataKey(prefabName, frameId)}{Extension}";

        /// <summary>{prefabName}@{frameId} — the PreviewMetadataStore key for one frame.</summary>
        public static string MetadataKey(string prefabName, string frameId) =>
            $"{prefabName}{Separator}{frameId}";

        /// <summary>
        /// Parse "{prefabName}@{frameId}" from a file name or asset path (the
        /// extension is optional). The split is on the LAST '@' because frame ids
        /// can never contain one; the frame id must be a valid id.
        /// </summary>
        public static bool TryParse(string fileName, out string prefabName, out string frameId)
        {
            prefabName = null;
            frameId = null;
            if (string.IsNullOrEmpty(fileName)) return false;

            string stem = Path.GetFileName(fileName.Replace('\\', '/'));
            if (stem.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - Extension.Length);
            int separator = stem.LastIndexOf(Separator);
            if (separator <= 0 || separator == stem.Length - 1) return false;

            string candidateFrame = stem.Substring(separator + 1);
            if (!AttractionTemplate.IsValidFrameId(candidateFrame)) return false;
            prefabName = stem.Substring(0, separator);
            frameId = candidateFrame;
            return true;
        }

        /// <summary>True for any asset under a content folder's Previews/Frames/.</summary>
        public static bool IsFramePreviewPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = assetPath.Replace('\\', '/');
            return normalized.IndexOf($"/Previews/{FolderName}/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
