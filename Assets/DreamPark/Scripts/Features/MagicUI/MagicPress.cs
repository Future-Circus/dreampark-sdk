using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// THE in-headset button behaviour (Oct 18 2026, Aidan: "gaze AND touch on all in-headset buttons"). Every Magic UI
    /// control (the start board, the Results choices, the next-up cards, the rating) presses the same two ways:
    ///   • GAZE-DWELL — always works (one hand is usually busy): look at it; a fill builds; brief flickers are forgiven.
    ///   • POKE — either hand's index fingertip pushes through its face (hand tracking, never controllers).
    /// Same feedback for both: the fill for a dwell, a press-in for a poke, the same confirm. Pure rules here
    /// (<see cref="MagicPressRules"/>, shared with the DreamBand's LanJoin pill), hands in <see cref="MagicHands"/>.
    /// </summary>
    public static class MagicPressRules
    {
        /// Brief gaze flickers shorter than this don't reset the dwell.
        public const float GazeGraceSeconds = 0.15f;
        /// Poke: the fingertip arms in front of the face (this far out) and presses on reaching it (this far behind
        /// the surface counts too); it must leave the front again before it can press again.
        public const float PokeArmM = 0.03f, PokePressM = 0.004f, PokeMarginM = 0.012f;

        /// One frame of dwell. `armed` = gaze has left the target since the last press (one press per look).
        public static float Step(float dwell, float dwellSeconds, ref float lostFor, ref bool armed, bool gazing, float dt, out bool fired)
        {
            fired = false;
            if (!gazing)
            {
                lostFor += dt;
                if (lostFor >= GazeGraceSeconds) { dwell = 0f; armed = true; }
                return dwell;
            }
            lostFor = 0f;
            if (!armed) return 0f;
            dwell += dt;
            if (dwell >= dwellSeconds) { fired = true; armed = false; return 0f; }
            return dwell;
        }

        /// Looking at it: within the cone and the reach.
        public static bool Gazing(Vector3 headPos, Vector3 headForward, Vector3 target, float coneDeg, float maxMetres)
        {
            Vector3 to = target - headPos;
            float d = to.magnitude;
            if (d < 1e-4f || d > maxMetres) return false;
            return Vector3.Angle(headForward, to) <= coneDeg;
        }

        /// Looking at a flat target of this half-size (metres, its plane): the gaze ray hits it (+ a small margin), or
        /// the cone catches its centre. A big card is looked at anywhere on its face, a small orb by the cone.
        public static bool GazingAt(Vector3 headPos, Vector3 headForward, Vector3 centre, Quaternion rotation, Vector2 half, float coneDeg, float maxMetres)
        {
            if (Gazing(headPos, headForward, centre, coneDeg, maxMetres)) return true;
            var plane = new Plane(rotation * Vector3.forward, centre);
            var ray = new Ray(headPos, headForward);
            if (!plane.Raycast(ray, out float t) || t > maxMetres) return false;
            Vector3 local = Conjugate(rotation) * (ray.GetPoint(t) - centre);
            return Mathf.Abs(local.x) <= half.x + 0.02f && Mathf.Abs(local.y) <= half.y + 0.02f;
        }

        /// The inverse of a unit rotation, in managed code (pure: no engine call).
        public static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        /// One frame of poke for one fingertip, in the target's LOCAL frame (its face is z = 0; the viewer's side is
        /// −z, Magic UI faces turn their back to the viewer). `armed` = the tip was seen in front of the face.
        public static bool PokeStep(Vector3 tipLocal, Vector2 half, ref bool armed)
        {
            bool inside = Mathf.Abs(tipLocal.x) <= half.x + PokeMarginM && Mathf.Abs(tipLocal.y) <= half.y + PokeMarginM;
            if (!inside) { armed = false; return false; }
            if (tipLocal.z < -PokeArmM) { armed = true; return false; }
            if (armed && tipLocal.z >= -PokePressM && tipLocal.z < 0.08f) { armed = false; return true; }
            return false;
        }

        /// How far a tip has pushed in (0 = at the arm distance or out, 1 = at the face): the press-in visual.
        public static float PokeDepth(Vector3 tipLocal, Vector2 half)
        {
            bool inside = Mathf.Abs(tipLocal.x) <= half.x + PokeMarginM && Mathf.Abs(tipLocal.y) <= half.y + PokeMarginM;
            if (!inside || tipLocal.z < -PokeArmM * 2f || tipLocal.z > 0.08f) return 0f;
            return Mathf.Clamp01(1f - (-tipLocal.z - PokePressM) / (PokeArmM * 2f - PokePressM));
        }
    }

    /// <summary>
    /// The tracked hands, as plain data: each hand's index fingertip and palm frame (wrist, and the index / little
    /// metacarpals: the rigid palm, no finger articulation). Reads OVRSkeleton (OpenXR or OVR hand skeletons),
    /// refreshed once a second. Harnesses and tests set <see cref="Synthetic"/> instead.
    /// </summary>
    public static class MagicHands
    {
        public struct Hand
        {
            public bool valid, left;
            public Vector3 wrist, indexTip, indexKnuckle, littleKnuckle, middleKnuckle;
        }

        /// Harness / test hands (non-null wins over the tracked ones).
        public static List<Hand> Synthetic;

#if !UNITY_IOS || UNITY_EDITOR
        // The iOS player is built without the Meta assemblies (IosQuestAssemblyFilter): OVR use stays Quest/Editor only.
        static OVRSkeleton[] s_skeletons = Array.Empty<OVRSkeleton>();
        static float s_nextSearch;
#endif
        static int s_frame = -1;
        static readonly List<Hand> s_hands = new List<Hand>(2);

        public static IReadOnlyList<Hand> Now()
        {
            if (Synthetic != null) return Synthetic;
            if (s_frame == Time.frameCount) return s_hands;
            s_frame = Time.frameCount;
            s_hands.Clear();
#if !UNITY_IOS || UNITY_EDITOR
            try
            {
                // CACHED (Nov 2 2026, flags-perf-nov2 — Quest main thread): the skeletons live as long as the rig, so
                // the scene-wide search runs every 5 s, at once when a cached one was destroyed (rig rebuilt), every
                // 1 s while none is found — not every second regardless (DepthMaskCeiling does the same).
                bool destroyed = false;
                for (int i = 0; i < s_skeletons.Length && !destroyed; i++) destroyed = s_skeletons[i] == null;
                if (Time.unscaledTime >= s_nextSearch || destroyed)
                {
                    s_skeletons = UnityEngine.Object.FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
                    s_nextSearch = Time.unscaledTime + (s_skeletons.Length > 0 ? 5f : 1f);
                }
                foreach (OVRSkeleton sk in s_skeletons)
                {
                    if (sk == null || !sk.IsInitialized || !sk.IsDataValid || sk.Bones == null) continue;
                    var type = sk.GetSkeletonType();
                    bool xr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
                    bool hand = xr || type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.HandRight;
                    if (!hand) continue;
                    // BoneId values overlap between skeleton types: compare only within the right set.
                    var wristId = xr ? OVRSkeleton.BoneId.XRHand_Wrist : OVRSkeleton.BoneId.Hand_WristRoot;
                    var tipId = xr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
                    var indexId = xr ? OVRSkeleton.BoneId.XRHand_IndexMetacarpal : OVRSkeleton.BoneId.Hand_Index1;
                    var littleId = xr ? OVRSkeleton.BoneId.XRHand_LittleMetacarpal : OVRSkeleton.BoneId.Hand_Pinky0;
                    var middleId = xr ? OVRSkeleton.BoneId.XRHand_MiddleMetacarpal : OVRSkeleton.BoneId.Hand_Middle1;
                    var h = new Hand { left = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.HandLeft };
                    int got = 0;
                    foreach (var b in sk.Bones)
                    {
                        if (b == null || b.Transform == null) continue;
                        if (b.Id == wristId) { h.wrist = b.Transform.position; got |= 1; }
                        else if (b.Id == tipId) { h.indexTip = b.Transform.position; got |= 2; }
                        else if (b.Id == indexId) { h.indexKnuckle = b.Transform.position; got |= 4; }
                        else if (b.Id == littleId) { h.littleKnuckle = b.Transform.position; got |= 8; }
                        else if (b.Id == middleId) { h.middleKnuckle = b.Transform.position; got |= 16; }
                    }
                    h.valid = got == 31;
                    if (h.valid) s_hands.Add(h);
                }
            }
            catch (Exception) { }
#endif
            return s_hands;
        }
    }

    /// <summary>
    /// One control's press state: gaze-dwell + poke, per <see cref="MagicPressRules"/>. Call <see cref="Tick"/> every frame
    /// with the control's transform and half-size (metres, in its plane). Returns true once per press.
    /// </summary>
    public sealed class MagicPress
    {
        public const float DefaultDwell = 1.6f, ConeDeg = 7f, MaxGazeM = 7f;
        /// Harnesses / recordings: a scripted camera must not press what it happens to look at.
        public static bool GazeEnabled = true;
        readonly float _dwellSeconds;
        float _dwell, _lostFor;
        bool _armed = true;
        readonly bool[] _pokeArmed = new bool[4];

        /// 0..1 of the dwell (the fill), 0..1 of a poke's push (the press-in), and when it last fired.
        public float DwellK { get; private set; }
        public float PokeK { get; private set; }
        public float FiredAt { get; private set; } = -10f;
        public bool LastWasPoke { get; private set; }

        public MagicPress(float dwellSeconds = DefaultDwell) { _dwellSeconds = dwellSeconds; }

        public bool Tick(Transform t, Vector2 half, float dt)
        {
            if (t == null) return false;
            bool fired = false;
            Camera cam = Camera.main;
            bool gazing = GazeEnabled && cam != null && MagicPressRules.GazingAt(cam.transform.position, cam.transform.forward, t.position, t.rotation,
                Vector2.Scale(half, new Vector2(t.lossyScale.x, t.lossyScale.y)), ConeDeg, MaxGazeM);
            _dwell = MagicPressRules.Step(_dwell, _dwellSeconds, ref _lostFor, ref _armed, gazing, dt, out bool dwelt);
            DwellK = Mathf.Clamp01(_dwell / _dwellSeconds);
            if (dwelt) { fired = true; LastWasPoke = false; }
            // Poke: either hand's index fingertip through the face (local frame; the face's half-size in metres).
            float push = 0f;
            IReadOnlyList<MagicHands.Hand> hands = MagicHands.Now();
            Vector3 s = t.lossyScale;
            for (int i = 0; i < hands.Count && i < _pokeArmed.Length; i++)
            {
                if (!hands[i].valid) continue;
                Vector3 local = MagicPressRules.Conjugate(t.rotation) * (hands[i].indexTip - t.position);
                if (MagicPressRules.PokeStep(local, Vector2.Scale(half, new Vector2(s.x, s.y)), ref _pokeArmed[i]) && !fired) { fired = true; LastWasPoke = true; }
                push = Mathf.Max(push, MagicPressRules.PokeDepth(local, Vector2.Scale(half, new Vector2(s.x, s.y))));
            }
            PokeK = push;
            if (fired) { FiredAt = Time.time; _dwell = 0f; DwellK = 0f; }
            return fired;
        }

        /// The confirm flash (1 → 0 over half a second after a press).
        public float Confirm => Mathf.Clamp01(1f - (Time.time - FiredAt) / 0.5f);
    }
}
