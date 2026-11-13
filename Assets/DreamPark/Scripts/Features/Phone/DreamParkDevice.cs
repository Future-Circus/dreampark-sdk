using UnityEngine;

namespace DreamPark
{
    /// <summary>What kind of device this player is on. Integer values are stable.</summary>
    public enum DreamParkDeviceKind { Headset = 0, Phone = 1, Editor = 2 }

    /// <summary>
    /// Which device the LOCAL player is on: a headset (hands, room-scale body),
    /// a phone (an AR window onto the park, touch input, no hands), or the
    /// Unity Editor.
    ///
    /// Core sets it once at boot (iOS → Phone, Android → Headset). When nothing
    /// sets it, the default is Editor in the Editor, Phone on an iOS player and
    /// Headset everywhere else, so SDK content and tests get a sensible answer
    /// without a host app.
    ///
    /// The Editor behaves as a headset (<see cref="IsHeadset"/> is true) unless
    /// <see cref="SimulatePhone"/> is on — the `DreamPark → Simulate Phone
    /// Player` menu toggle, applied when Play mode starts. Then the Editor
    /// reports Phone everywhere: dp.device() is "phone", the Park Simulator
    /// spawns the Phone Player instead of Player, and the mouse produces
    /// gestures (DreamParkEditorMouseGestures).
    ///
    /// Lua: dp.device() / dp.is_phone() / dp.is_headset() (DreamParkLuaAPI).
    /// </summary>
    public static class DreamParkDevice
    {
        static bool _isSet;
        static DreamParkDeviceKind _kind;

        /// <summary>Host apps call this at boot. Overrides the platform default.</summary>
        public static void Set(DreamParkDeviceKind kind)
        {
            _kind = kind;
            _isSet = true;
        }

        /// <summary>Forget any <see cref="Set"/> call and return to the platform default. For tests and tools.</summary>
        public static void ResetToDefault()
        {
            _isSet = false;
            _kind = DefaultKind;
        }

        /// <summary>The platform default used when <see cref="Set"/> was never called.</summary>
        public static DreamParkDeviceKind DefaultKind
        {
            get
            {
#if UNITY_EDITOR
                return DreamParkDeviceKind.Editor;
#elif UNITY_IOS
                return DreamParkDeviceKind.Phone;
#else
                return DreamParkDeviceKind.Headset;
#endif
            }
        }

        /// <summary>The local device. In the Editor, <see cref="SimulatePhone"/> wins over everything.</summary>
        public static DreamParkDeviceKind Current
        {
            get
            {
#if UNITY_EDITOR
                if (SimulatePhone) return DreamParkDeviceKind.Phone;
#endif
                return _isSet ? _kind : DefaultKind;
            }
        }

        public static bool IsPhone => Current == DreamParkDeviceKind.Phone;

        /// <summary>True on a headset, and in the Editor unless it is simulating a phone.</summary>
        public static bool IsHeadset
        {
            get
            {
                var k = Current;
                return k == DreamParkDeviceKind.Headset || k == DreamParkDeviceKind.Editor;
            }
        }

        /// <summary>
        /// Editor only: behave as a phone. Set by the `DreamPark → Simulate
        /// Phone Player` menu toggle on entering Play mode; ignored in players.
        /// </summary>
        public static bool SimulatePhone { get; set; }

        /// <summary>Wire / Lua name: "phone" | "headset" | "editor".</summary>
        public static string Wire(DreamParkDeviceKind k)
        {
            switch (k)
            {
                case DreamParkDeviceKind.Phone: return "phone";
                case DreamParkDeviceKind.Editor: return "editor";
                default: return "headset";
            }
        }

        /// <summary>
        /// What other players are told about this device: "phone" or "headset".
        /// The Editor counts as a headset unless it is simulating a phone, the
        /// same rule as <see cref="IsHeadset"/>.
        /// </summary>
        public static string PeerWire => IsPhone ? "phone" : "headset";

        /// <summary>
        /// LIVE CAMERA (Nov 13 2026): this headset is a portal's live camera — a NON-PLAYER that still sees, draws
        /// and records everything. The host app sets it (core: from Camera Mode, the per-headset MDM-lite switch).
        /// A camera stays a headset everywhere it renders (<see cref="IsHeadset"/>, <see cref="PeerWire"/>), but
        /// in multiplayer it is a spectator like a phone without a rig: it never leads or ranks, never writes a
        /// title's shared state, its net_send / dp.set_state are dropped (DreamParkParticipation), and the room
        /// sees it as "camera" in presence (<see cref="PresenceWire"/>) so players' headsets draw a camera tag
        /// over it instead of a player (RemoteNameTag). Off by default; the SDK never sets it.
        /// </summary>
        public static bool IsCamera { get; set; }

        /// <summary>Takes no part in any title's gameplay (a live camera). Phones decide per title (their rig).</summary>
        public static bool NeverParticipates => IsCamera;

        /// <summary>The presence "dv" this device sends: "camera" for a live camera, else <see cref="PeerWire"/>.</summary>
        public static string PresenceWire => IsCamera ? CameraWire : PeerWire;

        /// <summary>Presence device type of a live camera (RemotePlayer.device).</summary>
        public const string CameraWire = "camera";
    }
}
