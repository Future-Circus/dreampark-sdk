#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace DreamPark.PreUploadChecks
{
    /// <summary>
    /// Pure scoping rules shared by pre-upload checks. Kept outside the checks' own
    /// !DREAMPARKCORE guard so they are unit-tested in core too (PreUploadScopesTests).
    /// </summary>
    public static class PreUploadScopes
    {
        /// <summary>
        /// SDK / Meta shaders that deliberately have no occlusion (MetaOcclusionCheck).
        /// Both MRUK occluders are z-only geometric occluders (Oct 2026: the un-culled
        /// "InvisibleOccluder" was missing and DreamSports / Wizard's Way were flagged).
        /// </summary>
        public static readonly IReadOnlyCollection<string> OcclusionExemptShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Meta/EnvironmentDepth/DepthMask",                      // produces depth, isn't occluded by it
            "Meta/MRUK/MixedReality/InvisibleOccluderCulled",       // z-only geometric occluder
            "Meta/MRUK/MixedReality/InvisibleOccluder",             // same occluder, no culling
        };

        public static bool IsOcclusionExempt(string shaderName)
            => !string.IsNullOrEmpty(shaderName) && ((HashSet<string>)OcclusionExemptShaders).Contains(shaderName);

        /// <summary>
        /// Scenes the scene-overrides check must not open: vendor demo/example scenes under
        /// a content folder's ThirdParty/ (or ThirdPartyLocal/). They never ship, their
        /// overrides are the vendor's, and opening them logged ~93 "Missing Prefab" errors
        /// per run (DreamSports / Wizard's Way, Oct 2026). The project's own scenes,
        /// including test scenes anywhere else in the content folder, are still scanned.
        /// </summary>
        public static bool IsVendorScene(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return false;
            string p = scenePath.Replace('\\', '/');
            return p.IndexOf("/ThirdParty/", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The scenes the scene-overrides check reads: inside this package's content folder
        /// (Assets/Content/{id}) and not a vendor scene. A scene anywhere else in the project
        /// (Assets/Scenes/Main.unity, an Untitled scene) is never opened, read or reported.
        /// </summary>
        public static bool IsSceneOverridesScope(string scenePath, string contentRoot)
        {
            if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(contentRoot)) return false;
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return false;
            if (!global::DreamPark.DreamParkAutomation.IsUnder(scenePath, contentRoot)) return false;
            return !IsVendorScene(scenePath);
        }
    }
}
#endif
