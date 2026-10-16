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
    /// Core stamps this on the active Saved Location's root
    /// (ParkLocationRoot) — the transform VPS, QR and the Quest device anchor
    /// actually place, and the parent of every attraction. NOT the
    /// ParkAnchor: under saved locations localization moves the location
    /// root, and the park only follows it through a per-device checkpoint
    /// relation (iOS only), so two headsets' parks sit in different places
    /// while their content agrees. Poses sent park-relative then landed
    /// metres from the player they belonged to (Oct 7 2026).
    ///
    /// Where it is absent (an SDK test scene, a legacy park that moves the
    /// ParkAnchor itself, an older core) <see cref="Resolve"/> falls back to
    /// the topmost ancestor of the active PlayerRig, and to null in a scene
    /// with no park at all — in which case every conversion is the identity
    /// and the same numbers still work at a desk.
    /// </summary>
    [DisallowMultipleComponent]
    public class ParkRoot : MonoBehaviour
    {
        // Newest enabled root wins. A location switch stands the new root up
        // before the old one's deferred Destroy lands, so this is a list, not
        // a slot: the old root leaving must not clear the new one.
        static readonly System.Collections.Generic.List<ParkRoot> s_live = new System.Collections.Generic.List<ParkRoot>();

        public static ParkRoot Current
        {
            get
            {
                for (int i = s_live.Count - 1; i >= 0; i--)
                {
                    if (s_live[i] != null) return s_live[i];
                    s_live.RemoveAt(i);
                }
                return null;
            }
        }

        void OnEnable()
        {
            s_live.Remove(this);
            s_live.Add(this);
            _cached = null;
        }

        void OnDisable()
        {
            s_live.Remove(this);
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

        // ── Shared-frame state (Oct 8 2026, NetPose) ────────────────
        //
        // A park-local number is only meaningful when THIS device's frame is
        // actually placed in the room. Each headset localizes on its own (QR,
        // VPS, its own spatial anchor, a phone placement); before it has, the
        // location root sits wherever it booted and a pose sent relative to it
        // lands metres from the person (demo, Oct 8 2026: "the multiplayer
        // player positions were not tracking to the person"). Core installs
        // StateProvider (ParkLocationRoot.cs → ParkLocationNetFrame); without
        // one (SDK test scene, Park Simulator, a desk) the frame is trusted, as
        // it always was.

        public struct FrameState
        {
            /// <summary>Which shared frame this is (core: the Saved Location id). "" = unknown, matches any.</summary>
            public string id;
            /// <summary>This device's frame is placed in the room, so park-local numbers from it are real.</summary>
            public bool localized;
            /// <summary>Bumps when the frame jumps (a relocalization, a QR/VPS correction): receivers snap instead of gliding.</summary>
            public int epoch;
            /// <summary>The park is only shown as a miniature (relocalize diorama): remote players are drawn by the diorama camera alone.</summary>
            public bool miniature;
            /// <summary>What localized it (trace only).</summary>
            public string source;
        }

        /// <summary>Installed by the host app (core). Null = trust the frame (SDK, simulator, desk).</summary>
        public static System.Func<FrameState> StateProvider;

        static bool s_providerFailed;

        public static FrameState State
        {
            get
            {
                var provider = StateProvider;
                if (provider != null)
                {
                    try { return provider(); }
                    catch (System.Exception e)
                    {
                        if (!s_providerFailed) Debug.LogWarning("[ParkRoot] frame state provider threw: " + e.Message);
                        s_providerFailed = true;
                        // Fail closed: an unknown frame must not broadcast poses that look real.
                        return new FrameState { id = "", localized = false, source = "provider-error" };
                    }
                }
                return new FrameState { id = "", localized = true, source = Current != null ? "park-root" : "no-provider" };
            }
        }

        // ── Pure conversions (rigid, unit-scale frame; testable without a scene) ──
        // The same maths as ToPark / FromPark on a Transform with unit scale. The
        // inverse of a unit rotation is its conjugate — written out so these stay
        // managed code (Quaternion.Inverse is an engine call) and run anywhere.

        static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        public static Vector3 PointToFrame(Vector3 framePos, Quaternion frameRot, Vector3 world)
            => Conjugate(frameRot) * (world - framePos);

        public static Vector3 PointFromFrame(Vector3 framePos, Quaternion frameRot, Vector3 local)
            => framePos + frameRot * local;

        public static Quaternion RotToFrame(Quaternion frameRot, Quaternion world)
            => Conjugate(frameRot) * world;

        public static Quaternion RotFromFrame(Quaternion frameRot, Quaternion local)
            => frameRot * local;

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
