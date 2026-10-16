using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Where the player's head is, when it is not the camera.
    ///
    /// The SDK treats Camera.main as the player's head. On a headset that is
    /// always true. A host app can put the player's head somewhere else for a
    /// while, and point this at it: the phone's playtest agent does this
    /// (dreampark-core Assets/Scripts/PlaytestAgent). The agent walks the park
    /// as the signed-in player while Camera.main stays the phone's AR view.
    ///
    /// Two readers consult it:
    ///   • GameArea — which zone the player is in (starts games, opens
    ///     ContentGate, drives the adventure journal and rig switching).
    ///   • dp.head() — Lua content, and PlayerPresence (what other headsets see).
    ///
    /// Null (the default) means Camera.main, which is the behavior before this
    /// existed. Nothing in the SDK sets it.
    /// </summary>
    public static class PlayerHead
    {
        public static Transform Override;

        // ── Is the local player IN the park? (Oct 11 2026, dreampark-core core-playergate) ──
        //
        // The host sets LocalPlayerAbsent while the local player is not in the park: the
        // park is a diorama / relocalizing (not landed), or the phone shows the miniature.
        // The park keeps running live meanwhile, so content must not see the real camera:
        // it stands in the room, right inside the invisible full-size park (playtest:
        // pengos threw eggs at a headset whose park was still the diorama).
        // Content-facing readers go through ContentCamera(): Lua's Camera.main and dp.head(),
        // dp.local_player_present(), and the SDK's player-targeting components (EasyDetect,
        // EasyChase, EasyTarget, EasyUndetect, EasyLookAt, StandardEntity player ranges).
        // Camera.main itself (C#) is NEVER changed: rendering, XR, AR, the diorama's own
        // camera trick and the host's tools all keep reading the real camera.
        // Default false = the behavior before this existed. Nothing in the SDK sets it.

        /// Far from any park on every axis (XZ-only range checks included); y below -1000
        /// like every other limbo head (RemotePlayer.LimboLocal, the host's LocalPlayerGate).
        public static readonly Vector3 AbsentPosition = new Vector3(10000f, -10000f, 10000f);

        private static bool s_absent;
        private static Camera s_standIn;

        public static bool LocalPlayerAbsent
        {
            get => s_absent;
            set
            {
                if (s_absent == value) return;
                s_absent = value;
                if (s_standIn != null) SyncStandIn();
            }
        }

        public static bool LocalPlayerPresent => !s_absent;

        /// What content should treat as the player's camera: Camera.main while present;
        /// while absent a stand-in Camera that never renders, parked at AbsentPosition.
        /// The stand-in is the same object every time and rides the real head (identity
        /// child) once the player is present again, so a reference content cached while
        /// absent (an attraction that woke inside the diorama) follows the player after
        /// the landing.
        public static Camera ContentCamera() => s_absent ? SyncStandIn() : Camera.main;

        /// The stand-in (null until content first asked while absent). Diagnostics.
        public static Camera StandIn => s_standIn;

        private static Camera SyncStandIn()
        {
            if (s_standIn == null)
            {
                var go = new GameObject("DreamPark PlayerHead stand-in");
                go.hideFlags = HideFlags.HideInHierarchy;
                Object.DontDestroyOnLoad(go);
                s_standIn = go.AddComponent<Camera>();
                s_standIn.enabled = false;              // never renders
                s_standIn.cullingMask = 0;              // and draws nothing if something enables it
                s_standIn.clearFlags = CameraClearFlags.Nothing;
                s_standIn.depth = -1000f;
            }
            Transform t = s_standIn.transform;
            if (s_absent)
            {
                if (t.parent != null) { t.SetParent(null, false); Object.DontDestroyOnLoad(t.gameObject); }
                t.SetPositionAndRotation(AbsentPosition, Quaternion.identity);
                return s_standIn;
            }
            Camera real = Camera.main;
            Transform head = Override != null ? Override : real != null ? real.transform : null;
            if (head != null && t.parent != head)
            {
                t.SetParent(head, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            if (real != null)
            {
                s_standIn.fieldOfView = real.fieldOfView;
                s_standIn.nearClipPlane = real.nearClipPlane;
                s_standIn.farClipPlane = real.farClipPlane;
                s_standIn.orthographic = real.orthographic;
                s_standIn.orthographicSize = real.orthographicSize;
            }
            return s_standIn;
        }
    }
}
