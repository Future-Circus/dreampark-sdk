#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;

namespace DreamPark
{
    // How the SDK groups assets into bundles.
    //
    // Legacy: every top-level folder under Assets/Content/{gameId}/ becomes
    // one PackTogether bundle. Simple, predictable, robust. The downside is
    // that changing a single asset invalidates the entire group's bundle —
    // edit one texture and every player re-downloads the whole Textures
    // bundle.
    //
    // Smart: dependency-aware grouping. Each user-facing root (prefab, scene)
    // gets its own bundle along with the deps it uniquely owns. Deps shared
    // by multiple roots land in a Shared bundle. The result: a one-asset
    // change invalidates one bundle (typically a few MB) instead of a folder-
    // level bundle (potentially hundreds of MB). Stacks with the upload-skip
    // logic in ContentAPI to ship KB-scale patches and MB-scale new content.
    //
    // Smart is currently marked experimental — the algorithm needs validation
    // on real game content for edge cases (Lua-referenced audio addresses,
    // shared materials, package deps). Keep Legacy as the default until you've
    // verified Smart works end-to-end on your title.
    public enum BundlingStrategy
    {
        Legacy = 0,
        Smart = 1,
    }

    public static class BundlingStrategyPrefs
    {
        public const string PrefKey = "DreamPark.ContentUploader.BundlingStrategy";

        public static BundlingStrategy Current
        {
            get
            {
                int v = EditorPrefs.GetInt(PrefKey, (int)BundlingStrategy.Legacy);
                return (BundlingStrategy)v;
            }
            set
            {
                EditorPrefs.SetInt(PrefKey, (int)value);
            }
        }

        public static string Label(BundlingStrategy s)
        {
            switch (s)
            {
                case BundlingStrategy.Legacy: return "Legacy (PackTogether per folder)";
                case BundlingStrategy.Smart:  return "Smart (dependency-aware) [experimental]";
                default: return s.ToString();
            }
        }
    }
}
#endif
