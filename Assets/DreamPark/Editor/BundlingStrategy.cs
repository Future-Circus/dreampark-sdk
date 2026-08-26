#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // How the SDK groups assets into bundles.
    //
    // Smart (the default, and the only strategy exposed in the UI):
    // dependency-aware grouping. Each user-facing root (prefab, scene) gets
    // its own bundle along with the deps it uniquely owns. Deps shared by
    // multiple roots land in a Shared bundle. The result: a one-asset change
    // invalidates one bundle (typically a few MB) instead of a folder-level
    // bundle (potentially hundreds of MB). It is also what makes the upload
    // modes beyond a full re-upload possible at all — Patch needs
    // per-asset bundle granularity to be worth anything, and Code-only
    // needs the {gameId}-Code carve-out group that only Smart produces.
    //
    // Legacy (DEPRECATED, August 2026): every top-level folder under
    // Assets/Content/{gameId}/ becomes one PackTogether bundle. Simple and
    // predictable, but changing a single asset invalidates the entire
    // group's bundle — edit one texture and every player re-downloads the
    // whole Textures bundle. It also silently disables the upload-mode
    // picker (ContentUploadFlowPopup.DrawUploadModeCard), so every upload
    // ships everything.
    //
    // Legacy is on its way out. The enum member and its code paths are kept
    // so in-flight projects and support can fall back, but there is no
    // discoverable UI for it: DreamPark ▸ Troubleshooting ▸ Use Legacy
    // Bundling (deprecated) is the only switch, and a one-time migration
    // (below) moves everyone who was sitting on the old Legacy default over
    // to Smart. Do not add it back to the Content Uploader panel.
    public enum BundlingStrategy
    {
        Legacy = 0,
        Smart = 1,
    }

    public static class BundlingStrategyPrefs
    {
        public const string PrefKey = "DreamPark.ContentUploader.BundlingStrategy";

        // Stamped with CurrentMigration once a machine has been migrated OR
        // once the user has made an explicit choice (see the setter). Both
        // count as "this machine's strategy is settled", which is what stops
        // the migration from stomping a deliberate Legacy fallback on the
        // next domain reload.
        internal const string MigrationPrefKey = "DreamPark.ContentUploader.BundlingStrategy.Migration";

        // Bump this if a future migration needs to re-run on machines that
        // already went through migration 1.
        internal const int CurrentMigration = 1;

        public static BundlingStrategy Current
        {
            get
            {
                int v = EditorPrefs.GetInt(PrefKey, (int)BundlingStrategy.Smart);
                // Defensive: anything that isn't a defined member resolves to
                // Smart rather than falling through to Legacy's 0.
                if (v != (int)BundlingStrategy.Legacy && v != (int)BundlingStrategy.Smart)
                {
                    return BundlingStrategy.Smart;
                }
                return (BundlingStrategy)v;
            }
            set
            {
                EditorPrefs.SetInt(PrefKey, (int)value);
                // An explicit choice is a settled choice — including a
                // deliberate switch back to Legacy, which the migration must
                // not undo.
                EditorPrefs.SetInt(MigrationPrefKey, CurrentMigration);
            }
        }

        public static string Label(BundlingStrategy s)
        {
            switch (s)
            {
                case BundlingStrategy.Legacy: return "Legacy (PackTogether per folder) [deprecated]";
                case BundlingStrategy.Smart:  return "Smart (dependency-aware)";
                default: return s.ToString();
            }
        }
    }

    // One-time move off the old Legacy default.
    //
    // The strategy lives in EditorPrefs, which is per-machine and not in the
    // repo, so "the default is now Smart" only reaches someone who has never
    // touched the picker. Everyone who ever opened the Content Uploader
    // before this change has an explicit 0 stored — and with the picker gone
    // from the panel they would have had no way back. So: rewrite a stored
    // Legacy to Smart exactly once, then never again.
    [InitializeOnLoad]
    internal static class BundlingStrategyMigration
    {
        static BundlingStrategyMigration()
        {
            // Deferred so the log lands after the domain reload settles
            // rather than in the middle of it.
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            if (EditorPrefs.GetInt(BundlingStrategyPrefs.MigrationPrefKey, 0)
                >= BundlingStrategyPrefs.CurrentMigration)
            {
                return;
            }

            bool hadStoredValue = EditorPrefs.HasKey(BundlingStrategyPrefs.PrefKey);
            int stored = EditorPrefs.GetInt(BundlingStrategyPrefs.PrefKey, (int)BundlingStrategy.Smart);
            bool wasLegacy = hadStoredValue && stored == (int)BundlingStrategy.Legacy;

            EditorPrefs.SetInt(BundlingStrategyPrefs.PrefKey, (int)BundlingStrategy.Smart);
            EditorPrefs.SetInt(BundlingStrategyPrefs.MigrationPrefKey, BundlingStrategyPrefs.CurrentMigration);

            if (wasLegacy)
            {
                Debug.Log(
                    "[ContentUploader] Bundling strategy migrated: Legacy → Smart.\n" +
                    "Smart (dependency-aware) bundling is now the default. Your next build will look " +
                    "like a full re-upload because every asset moves to a new group — that is expected, " +
                    "and it is a one-off. After it, the Compile & Upload window gains an Upload Scope " +
                    "picker with Patch (changed files only) and Code only (Lua bundle).\n" +
                    "Legacy is deprecated. If you need it back: DreamPark ▸ Troubleshooting ▸ " +
                    "Use Legacy Bundling (deprecated).");
            }
        }
    }

    // The only remaining way to select Legacy. Deliberately parked at the
    // bottom of the Troubleshooting menu, well away from the shipping path,
    // and rendered as a checked toggle so its state is visible at a glance.
    internal static class BundlingStrategyMenu
    {
        private const string MenuPath = "DreamPark/Troubleshooting/Use Legacy Bundling (deprecated)";

        [MenuItem(MenuPath, false, 260)]
        private static void ToggleLegacy()
        {
            bool legacyNow = BundlingStrategyPrefs.Current == BundlingStrategy.Legacy;

            if (!legacyNow)
            {
                bool ok = EditorUtility.DisplayDialog(
                    "Switch back to Legacy bundling?",
                    "Legacy bundling is deprecated and will be removed in a future SDK release.\n\n" +
                    "On Legacy:\n" +
                    "  • Every upload is a full re-upload. The Upload Scope picker disappears and " +
                    "Patch / Code-only are unavailable.\n" +
                    "  • A one-asset edit invalidates its whole folder-level bundle, so guests " +
                    "re-download far more than changed.\n\n" +
                    "Your next build will look like a full re-upload either way, because the groups " +
                    "re-partition when the strategy changes.\n\n" +
                    "Only do this if Smart bundling is actively breaking on your content — and please " +
                    "tell us if it is.",
                    "Switch to Legacy", "Cancel");
                if (!ok) return;

                BundlingStrategyPrefs.Current = BundlingStrategy.Legacy;
                Debug.LogWarning(
                    "[ContentUploader] Bundling strategy → Legacy (deprecated). Every upload from this " +
                    "machine will be a full re-upload until you switch back.");
            }
            else
            {
                BundlingStrategyPrefs.Current = BundlingStrategy.Smart;
                Debug.Log(
                    "[ContentUploader] Bundling strategy → Smart. The next build re-partitions every " +
                    "group, so expect one full re-upload before Patch uploads become small.");
            }
        }

        [MenuItem(MenuPath, true, 260)]
        private static bool ToggleLegacyValidate()
        {
            Menu.SetChecked(MenuPath, BundlingStrategyPrefs.Current == BundlingStrategy.Legacy);
            return true;
        }
    }
}
#endif
