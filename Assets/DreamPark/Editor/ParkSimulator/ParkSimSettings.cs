// ─────────────────────────────────────────────────────────────────────
//  ParkSimSettings.cs — Park Simulator preferences
//
//  EditorPrefs, not a ScriptableObject: the simulator is a per-developer
//  debugging tool, so its settings must not land in the project and get
//  committed, and must not differ between two people opening the same
//  scene. Keyed by project path so two checkouts on one machine keep
//  separate settings.
// ─────────────────────────────────────────────────────────────────────

using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public static class ParkSimSettings
    {
        private static string Key(string name)
        {
            return "DreamPark.ParkSim." + name + "." + Application.dataPath.GetHashCode();
        }

        /// Master switch, ON by default.
        ///
        /// Deliberate: an attraction that only ever runs alone at the origin on
        /// flat ground is not the thing that ships, and a creator who never sees
        /// it in a park does not find out until a venue does. Pressing Play
        /// should put them in the park. Stopping the simulation is a per-press
        /// action in the Scene view overlay, NOT a saved preference — see
        /// ParkSimulator.Stop — so the next Play is back in the park again.
        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(Key("Enabled"), true); }
            set { EditorPrefs.SetBool(Key("Enabled"), value); }
        }

        /// Re-frame the Scene view onto your attraction when the park builds,
        /// preserving the offset you were viewing it at (see ParkSimViewpoint).
        ///
        /// Only governs that automatic move. Go and Patrol are explicit
        /// presses, so they always work. Nothing here spawns anything —
        /// Simulator copies the Scene view onto Camera.main, and moving the
        /// Scene view is the whole mechanism.
        public static bool DriveCamera
        {
            get { return EditorPrefs.GetBool(Key("DriveCamera"), true); }
            set { EditorPrefs.SetBool(Key("DriveCamera"), value); }
        }

        /// Include the bundled Sample project's attractions and props.
        ///
        /// On by default: a creator who has not built anything yet still gets a
        /// populated park to fly through, which is what Sample is for. Turn it
        /// off once your own content is the thing you are testing and the
        /// example is just noise.
        public static bool IncludeSample
        {
            get { return EditorPrefs.GetBool(Key("IncludeSample"), true); }
            set { EditorPrefs.SetBool(Key("IncludeSample"), value); }
        }

        /// Include PropTemplate prefabs alongside attractions.
        public static bool IncludeProps
        {
            get { return EditorPrefs.GetBool(Key("IncludeProps"), true); }
            set { EditorPrefs.SetBool(Key("IncludeProps"), value); }
        }

        /// Replay cached floor data on regenerate instead of re-conforming from
        /// scratch, when an attraction lands back on a spawn point it has
        /// already been calibrated against. Exercises the LOAD path
        /// (LevelTemplate.floorData -> ApplyPendingCalibration) rather than the
        /// PLACEMENT path (ConformOnce), which is what a returning guest hits.
        public static bool ReplayCachedFloorData
        {
            get { return EditorPrefs.GetBool(Key("ReplayFloor"), true); }
            set { EditorPrefs.SetBool(Key("ReplayFloor"), value); }
        }

        /// 0 = reseed every generation. Anything else pins the layout so a bug
        /// found at a particular arrangement can be reproduced.
        public static int Seed
        {
            get { return EditorPrefs.GetInt(Key("Seed"), 0); }
            set { EditorPrefs.SetInt(Key("Seed"), value); }
        }

        // ── Menu ─────────────────────────────────────────────────────────

        private const string MenuEnabled = "DreamPark/Park Simulator/Simulate Park On Play";
        private const string MenuCamera  = "DreamPark/Park Simulator/Drive Camera From Scene View";
        private const string MenuProps   = "DreamPark/Park Simulator/Include Props";
        private const string MenuReplay  = "DreamPark/Park Simulator/Replay Cached Floor Data";
        private const string MenuSample  = "DreamPark/Park Simulator/Include Sample Content";

        [MenuItem(MenuEnabled, false, 60)]
        private static void ToggleEnabled() { Enabled = !Enabled; }
        [MenuItem(MenuEnabled, true, 60)]
        private static bool ToggleEnabledValidate() { Menu.SetChecked(MenuEnabled, Enabled); return true; }

        [MenuItem(MenuCamera, false, 61)]
        private static void ToggleCamera() { DriveCamera = !DriveCamera; }
        [MenuItem(MenuCamera, true, 61)]
        private static bool ToggleCameraValidate() { Menu.SetChecked(MenuCamera, DriveCamera); return true; }

        [MenuItem(MenuProps, false, 62)]
        private static void ToggleProps() { IncludeProps = !IncludeProps; }
        [MenuItem(MenuProps, true, 62)]
        private static bool TogglePropsValidate() { Menu.SetChecked(MenuProps, IncludeProps); return true; }

        [MenuItem(MenuSample, false, 63)]
        private static void ToggleSample() { IncludeSample = !IncludeSample; }
        [MenuItem(MenuSample, true, 63)]
        private static bool ToggleSampleValidate() { Menu.SetChecked(MenuSample, IncludeSample); return true; }

        [MenuItem(MenuReplay, false, 64)]
        private static void ToggleReplay() { ReplayCachedFloorData = !ReplayCachedFloorData; }
        [MenuItem(MenuReplay, true, 64)]
        private static bool ToggleReplayValidate() { Menu.SetChecked(MenuReplay, ReplayCachedFloorData); return true; }
    }
}
