using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// The one coordinate frame every headset in a park agrees on.
    ///
    /// On Quest, Unity world space is PER HEADSET: PortalAnchor's world sync
    /// moves the park to the QR code and leaves each headset's world origin
    /// wherever it booted. Two players who put their headsets on facing each
    /// other have world frames ~180 degrees apart, so a world-space number
    /// that crosses the relay lands mirrored in the other player's room.
    /// Attractions and props never notice because they are children of the
    /// park and only ever use local offsets. Anything networked has to do the
    /// same — and everything in the SDK that touches the wire (typed
    /// net_send, PlayerPresence, dp.to_park/from_park) converts through THIS.
    ///
    /// Core stamps this on the ParkAnchor when it creates one. Where it is
    /// absent (an SDK test scene, an older core) <see cref="Resolve"/> falls
    /// back to the topmost ancestor of the active PlayerRig, which is the
    /// same object on device, and to null in a scene with no park at all —
    /// in which case every conversion is the identity and the same numbers
    /// still work at a desk.
    /// </summary>
    [DisallowMultipleComponent]
    public class ParkRoot : MonoBehaviour
    {
        public static ParkRoot Current { get; private set; }

        void Awake()
        {
            if (Current != null && Current != this)
                Debug.LogWarning($"[ParkRoot] A second ParkRoot appeared on '{name}' while '{Current.name}' is live — using the newest.");
            Current = this;
            _cached = null;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            _cached = null;
        }

        static Transform _cached;

        /// <summary>
        /// The shared frame's Transform, or null when there is no park (identity).
        /// Cheap: cached, re-resolved only when the cached transform dies.
        /// </summary>
        public static Transform Resolve()
        {
            if (Current != null) return Current.transform;
            if (_cached != null) return _cached;

            // Topmost ancestor of a rig the core has parented into a park. In a
            // park that is the ParkAnchor; in the Park Simulator it is the sim's
            // park root; in a bare test scene the rig is itself a scene root and
            // there is no frame.
            var rig = PlayerRig.Instance;
            if (rig == null && PlayerRig.instances != null)
            {
                foreach (var kv in PlayerRig.instances)
                    if (kv.Value != null) { rig = kv.Value; break; }
            }
            if (rig == null || rig.transform.parent == null) return null;

            var t = rig.transform.parent;
            while (t.parent != null) t = t.parent;
            _cached = t;
            return _cached;
        }

        // ── Conversions (identity when there is no park) ─────────────

        public static Vector3 ToPark(Vector3 worldPoint)
        {
            var p = Resolve();
            return p != null ? p.InverseTransformPoint(worldPoint) : worldPoint;
        }

        public static Vector3 FromPark(Vector3 parkPoint)
        {
            var p = Resolve();
            return p != null ? p.TransformPoint(parkPoint) : parkPoint;
        }

        public static Vector3 ToParkDir(Vector3 worldDir)
        {
            var p = Resolve();
            return p != null ? p.InverseTransformDirection(worldDir) : worldDir;
        }

        public static Vector3 FromParkDir(Vector3 parkDir)
        {
            var p = Resolve();
            return p != null ? p.TransformDirection(parkDir) : parkDir;
        }

        public static Quaternion ToParkRot(Quaternion worldRot)
        {
            var p = Resolve();
            return p != null ? Quaternion.Inverse(p.rotation) * worldRot : worldRot;
        }

        public static Quaternion FromParkRot(Quaternion parkRot)
        {
            var p = Resolve();
            return p != null ? p.rotation * parkRot : parkRot;
        }
    }
}
