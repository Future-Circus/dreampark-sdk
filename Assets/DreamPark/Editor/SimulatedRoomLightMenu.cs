#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// `DreamPark → Simulated Room Light`: make dp.light read a chosen room in the Editor (Park
    /// Simulator included) — bright, dim, lights off — and a chosen light direction, so a "lights off"
    /// puzzle or a sundial can be checked without a headset. While on, dp.light.live() is true and
    /// confidence() is 1.
    ///
    /// Persisted in EditorPrefs (a property of the person testing, not of the content), applied again
    /// each time Play starts, never compiled into a player: on a device dp.light always reads the
    /// camera. From Lua: dp.light.debug_set(ambient[, dir]) / dp.light.debug_set(nil).
    /// </summary>
    [InitializeOnLoad]
    public static class SimulatedRoomLightMenu
    {
        const string Root = "DreamPark/Simulated Room Light/";
        public const string AmbientKey = "DreamPark.SimulatedRoomLight.Ambient";
        public const string AzimuthKey = "DreamPark.SimulatedRoomLight.Azimuth";
        public const string ElevationKey = "DreamPark.SimulatedRoomLight.Elevation";

        /// <summary>The saved simulated ambient 0..1, or -1 = the real estimate.</summary>
        public static float Ambient
        {
            get => EditorPrefs.GetFloat(AmbientKey, -1f);
            set { EditorPrefs.SetFloat(AmbientKey, value < 0f ? -1f : Mathf.Clamp01(value)); Apply(); }
        }

        /// <summary>Degrees clockwise from world +Z (90 = the light is toward +X).</summary>
        public static float Azimuth
        {
            get => EditorPrefs.GetFloat(AzimuthKey, 0f);
            set { EditorPrefs.SetFloat(AzimuthKey, Mathf.Repeat(value, 360f)); Apply(); }
        }

        /// <summary>Degrees above the horizon (90 = straight overhead).</summary>
        public static float Elevation
        {
            get => EditorPrefs.GetFloat(ElevationKey, 90f);
            set { EditorPrefs.SetFloat(ElevationKey, Mathf.Clamp(value, -90f, 90f)); Apply(); }
        }

        /// <summary>Unit vector toward the light for an azimuth / elevation in degrees.</summary>
        public static Vector3 Direction(float azimuthDeg, float elevationDeg)
        {
            float az = azimuthDeg * Mathf.Deg2Rad, el = elevationDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el)).normalized;
        }

        static void Apply() => DreamParkRoomLight.SetSimulated(Ambient, Direction(Azimuth, Elevation));

        static SimulatedRoomLightMenu()
        {
            Apply();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            Apply();   // a dp.light.debug_set from the last session does not carry over
            if (DreamParkRoomLight.Simulating)
                Debug.Log("[dp.light] Simulated room light: " + Label() +
                          " (DreamPark → Simulated Room Light). A device always reads its camera.");
        }

        internal static string Label()
        {
            float a = Ambient;
            if (a < 0f) return "off";
            return "ambient " + a.ToString("0.00") + ", light from az " + Azimuth.ToString("0") +
                   "° el " + Elevation.ToString("0") + "°";
        }

        static void SetAmbient(float a)
        {
            Ambient = a;
            Debug.Log("[dp.light] Simulated room light: " + (a < 0f ? "off (real estimate)" : Label()));
        }

        static void SetDir(float az, float el)
        {
            EditorPrefs.SetFloat(AzimuthKey, az);
            Elevation = el;
            if (Ambient < 0f) Ambient = 1f;   // picking a direction turns the simulation on
            Debug.Log("[dp.light] Simulated room light: " + Label());
        }

        static bool IsAmbient(float a) =>
            a < 0f ? !DreamParkRoomLight.Simulating
                   : DreamParkRoomLight.Simulating && Mathf.Abs(DreamParkRoomLight.SimulatedAmbient - a) < 0.005f;

        static bool IsDir(float az, float el) =>
            DreamParkRoomLight.Simulating && Vector3.Angle(DreamParkRoomLight.SimulatedDirection, Direction(az, el)) < 0.5f;

        [MenuItem(Root + "Off (real estimate)", false, 70)] static void Off() => SetAmbient(-1f);
        [MenuItem(Root + "Off (real estimate)", true)] static bool OffV() { Menu.SetChecked(Root + "Off (real estimate)", IsAmbient(-1f)); return true; }

        [MenuItem(Root + "Bright Room 1.0", false, 81)] static void Bright() => SetAmbient(1f);
        [MenuItem(Root + "Bright Room 1.0", true)] static bool BrightV() { Menu.SetChecked(Root + "Bright Room 1.0", IsAmbient(1f)); return true; }

        [MenuItem(Root + "Dim Lamp 0.35", false, 82)] static void Dim() => SetAmbient(0.35f);
        [MenuItem(Root + "Dim Lamp 0.35", true)] static bool DimV() { Menu.SetChecked(Root + "Dim Lamp 0.35", IsAmbient(0.35f)); return true; }

        [MenuItem(Root + "Lights Off 0.05", false, 83)] static void Dark() => SetAmbient(0.05f);
        [MenuItem(Root + "Lights Off 0.05", true)] static bool DarkV() { Menu.SetChecked(Root + "Lights Off 0.05", IsAmbient(0.05f)); return true; }

        [MenuItem(Root + "Light Overhead", false, 101)] static void Overhead() => SetDir(0f, 90f);
        [MenuItem(Root + "Light Overhead", true)] static bool OverheadV() { Menu.SetChecked(Root + "Light Overhead", IsDir(0f, 90f)); return true; }

        [MenuItem(Root + "Light Low From +X", false, 102)] static void LowX() => SetDir(90f, 20f);
        [MenuItem(Root + "Light Low From +X", true)] static bool LowXV() { Menu.SetChecked(Root + "Light Low From +X", IsDir(90f, 20f)); return true; }

        [MenuItem(Root + "Light Low From +Z", false, 103)] static void LowZ() => SetDir(0f, 20f);
        [MenuItem(Root + "Light Low From +Z", true)] static bool LowZV() { Menu.SetChecked(Root + "Light Low From +Z", IsDir(0f, 20f)); return true; }

        [MenuItem(Root + "Custom…", false, 120)]
        static void Custom() => SimulatedRoomLightWindow.Open();
    }

    /// <summary>The sliders behind DreamPark → Simulated Room Light → Custom….</summary>
    public sealed class SimulatedRoomLightWindow : EditorWindow
    {
        public static void Open()
        {
            var w = GetWindow<SimulatedRoomLightWindow>(true, "Simulated Room Light", true);
            w.minSize = w.maxSize = new Vector2(340, 170);
            w.Show();
        }

        void OnGUI()
        {
            float saved = SimulatedRoomLightMenu.Ambient;
            bool on = saved >= 0f;
            bool want = EditorGUILayout.Toggle("Simulate", on);
            float a = on ? saved : 1f, az = SimulatedRoomLightMenu.Azimuth, el = SimulatedRoomLightMenu.Elevation;
            using (new EditorGUI.DisabledScope(!want))
            {
                a = EditorGUILayout.Slider("Ambient (0 dark, 1 bright)", a, 0f, 1f);
                az = EditorGUILayout.Slider("Light azimuth (° from +Z)", az, 0f, 359.9f);
                el = EditorGUILayout.Slider("Light elevation (°)", el, -90f, 90f);
            }
            if (want != on || (want && Mathf.Abs(a - saved) > 0.0005f)) SimulatedRoomLightMenu.Ambient = want ? a : -1f;
            if (Mathf.Abs(az - SimulatedRoomLightMenu.Azimuth) > 0.01f) SimulatedRoomLightMenu.Azimuth = az;
            if (Mathf.Abs(el - SimulatedRoomLightMenu.Elevation) > 0.01f) SimulatedRoomLightMenu.Elevation = el;

            EditorGUILayout.Space(4);
            DreamParkRoomLight.Read(out var r);
            Vector3 d = r.direction;
            EditorGUILayout.LabelField("dp.light now reads",
                "ambient " + r.ambient.ToString("0.00") + "  dir (" + d.x.ToString("0.00") + ", " + d.y.ToString("0.00") + ", " +
                d.z.ToString("0.00") + ")" + (want ? "  (simulated)" : ""));
            EditorGUILayout.HelpBox("Editor and Park Simulator only. A headset or phone always reads its own camera.", MessageType.None);
        }

        void OnInspectorUpdate() => Repaint();
    }
}
#endif
