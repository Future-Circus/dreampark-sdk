#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// `DreamPark → Simulated Local Time`: make dp.time read another hour of
    /// the day in the Editor (Park Simulator included), so night content can
    /// be checked in the afternoon. The clock starts at the chosen hour when
    /// you pick it and again each time Play starts, then keeps running.
    ///
    /// Persisted in EditorPrefs (a property of the person testing, not of the
    /// content) and never compiled into a player: on a device dp.time always
    /// reads the real clock. From Lua: dp.time.debug_set(hour | nil).
    /// </summary>
    [InitializeOnLoad]
    public static class SimulatedLocalTimeMenu
    {
        const string Root = "DreamPark/Simulated Local Time/";
        public const string PrefKey = "DreamPark.SimulatedLocalHour";

        /// <summary>The saved simulated hour [0, 24), or -1 = the device clock.</summary>
        public static float Hour
        {
            get => EditorPrefs.GetFloat(PrefKey, -1f);
            set
            {
                EditorPrefs.SetFloat(PrefKey, value < 0f ? -1f : value % 24f);
                DreamParkTime.SetSimulatedHour(Hour);
            }
        }

        static SimulatedLocalTimeMenu()
        {
            DreamParkTime.SetSimulatedHour(Hour);
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            DreamParkTime.SetSimulatedHour(Hour);    // a dp.time.debug_set from the last session does not carry over
            if (DreamParkTime.Simulating)
                Debug.Log("[dp.time] Simulated local time " + Label(Hour) +
                          " (DreamPark → Simulated Local Time). A device always reads its real clock.");
        }

        internal static string Label(double hour)
        {
            if (hour < 0) return "off";
            int m = (int)System.Math.Round(hour * 60.0) % (24 * 60);
            return (m / 60).ToString("00") + ":" + (m % 60).ToString("00");
        }

        static void Set(float hour)
        {
            Hour = hour;
            Debug.Log("[dp.time] Simulated local time: " + (hour < 0 ? "off (device clock)" : Label(hour)));
        }

        static bool Is(float hour) =>
            hour < 0 ? !DreamParkTime.Simulating
                     : DreamParkTime.Simulating && Mathf.Abs((float)DreamParkTime.SimulatedHour - hour) < 0.01f;

        [MenuItem(Root + "Off (device clock)", false, 70)] static void Off() => Set(-1f);
        [MenuItem(Root + "Off (device clock)", true)] static bool OffV() { Menu.SetChecked(Root + "Off (device clock)", Is(-1f)); return true; }

        [MenuItem(Root + "Dawn 06:30", false, 81)] static void Dawn() => Set(6.5f);
        [MenuItem(Root + "Dawn 06:30", true)] static bool DawnV() { Menu.SetChecked(Root + "Dawn 06:30", Is(6.5f)); return true; }

        [MenuItem(Root + "Noon 12:00", false, 82)] static void Noon() => Set(12f);
        [MenuItem(Root + "Noon 12:00", true)] static bool NoonV() { Menu.SetChecked(Root + "Noon 12:00", Is(12f)); return true; }

        [MenuItem(Root + "Dusk 19:30", false, 83)] static void Dusk() => Set(19.5f);
        [MenuItem(Root + "Dusk 19:30", true)] static bool DuskV() { Menu.SetChecked(Root + "Dusk 19:30", Is(19.5f)); return true; }

        [MenuItem(Root + "Night 22:00", false, 84)] static void Night() => Set(22f);
        [MenuItem(Root + "Night 22:00", true)] static bool NightV() { Menu.SetChecked(Root + "Night 22:00", Is(22f)); return true; }

        [MenuItem(Root + "Midnight 00:00", false, 85)] static void Midnight() => Set(0f);
        [MenuItem(Root + "Midnight 00:00", true)] static bool MidnightV() { Menu.SetChecked(Root + "Midnight 00:00", Is(0f)); return true; }

        [MenuItem(Root + "Custom Hour…", false, 96)]
        static void Custom() => SimulatedLocalTimeWindow.Open();
    }

    /// <summary>The hour slider behind DreamPark → Simulated Local Time → Custom Hour….</summary>
    public sealed class SimulatedLocalTimeWindow : EditorWindow
    {
        float _hour = 12f;

        public static void Open()
        {
            var w = GetWindow<SimulatedLocalTimeWindow>(true, "Simulated Local Time", true);
            w.minSize = w.maxSize = new Vector2(320, 118);
            float saved = SimulatedLocalTimeMenu.Hour;
            if (saved >= 0f) w._hour = saved;
            w.Show();
        }

        void OnGUI()
        {
            bool on = DreamParkTime.Simulating;
            bool want = EditorGUILayout.Toggle("Simulate", on);
            using (new EditorGUI.DisabledScope(!want))
                _hour = EditorGUILayout.Slider("Local hour", _hour, 0f, 23.99f);
            if (want != on || (want && Mathf.Abs(_hour - SimulatedLocalTimeMenu.Hour) > 0.001f))
                SimulatedLocalTimeMenu.Hour = want ? _hour : -1f;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("dp.time.hour() now reads",
                SimulatedLocalTimeMenu.Label(DreamParkTime.LocalHour) + (want ? "  (simulated)" : "  (device clock)"));
            EditorGUILayout.HelpBox("Editor and Park Simulator only. A headset or phone always reads its own clock.", MessageType.None);
        }

        void OnInspectorUpdate() => Repaint();
    }
}
#endif
