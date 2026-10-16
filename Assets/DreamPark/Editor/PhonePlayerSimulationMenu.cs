#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// `DreamPark → Simulate Phone Player`: make the Editor behave as a phone
    /// in Play mode. dp.device() returns "phone", the Park Simulator spawns
    /// the content's Phone Player (or no rig when the slot is empty — phone
    /// users are spectators by default), and the mouse produces taps, drags
    /// and pinches (DreamParkEditorMouseGestures).
    ///
    /// Persisted in EditorPrefs and applied when Play mode starts; toggling
    /// during Play takes effect on the next Play.
    /// </summary>
    [InitializeOnLoad]
    public static class PhonePlayerSimulationMenu
    {
        public const string MenuPath = "DreamPark/Simulate Phone Player";
        public const string PrefKey = "DreamPark.SimulatePhonePlayer";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, false);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        static PhonePlayerSimulationMenu()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            // After a domain reload (including the one entering Play mode, when
            // reloads are on) static state is fresh: re-apply the preference.
            // Outside Play the runtime flag is cleared so edit-mode tools and
            // tests see the Editor as the Editor.
            DreamParkDevice.SimulatePhone = EditorApplication.isPlayingOrWillChangePlaymode && Enabled;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                case PlayModeStateChange.EnteredPlayMode:
                    DreamParkDevice.SimulatePhone = Enabled;
                    if (Enabled && change == PlayModeStateChange.EnteredPlayMode)
                        Debug.Log("[DreamPark] Simulating a phone player: dp.device() == 'phone', mouse → taps / drags / scroll-pinch. Toggle in DreamPark → Simulate Phone Player.");
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    DreamParkDevice.SimulatePhone = false;
                    break;
            }
        }

        [MenuItem(MenuPath, false, 60)]
        static void Toggle()
        {
            Enabled = !Enabled;
            Menu.SetChecked(MenuPath, Enabled);
            if (EditorApplication.isPlaying)
                Debug.Log("[DreamPark] Simulate Phone Player " + (Enabled ? "ON" : "OFF") + " — takes effect the next time you enter Play mode.");
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}
#endif
