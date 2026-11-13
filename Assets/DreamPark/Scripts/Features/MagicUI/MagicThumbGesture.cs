using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// THUMBS UP / DOWN BY HAND (Oct 18 2026, Aidan): on the rating step the player raises a hand in front of them and
    /// rolls it — thumb side up = 👍, the other way = 👎 — and holds it ~0.6 s. No finger tracking: the hand's ROLL comes
    /// from the rigid palm only (wrist → middle metacarpal = the fingers' direction; little → index metacarpal = the
    /// thumb side), so a fist, an open hand or a lazy thumb all read the same. Pure (testable with recorded or synthetic
    /// poses); <see cref="MagicThumbGesture"/> feeds it the tracked hands.
    ///   roll   0° = palm down · +90° = thumb side up · −90° = thumb side down · ±180° = palm up
    /// </summary>
    public static class MagicThumbRules
    {
        public const float RollMinDeg = 70f, RollMaxDeg = 110f;
        public const float HoldSeconds = 0.6f;
        /// In front of the face, within reach, above the waist.
        public const float MaxReachM = 0.6f, MinAheadM = 0.12f, AboveWaistBelowEyesM = 0.7f;
        /// Steady: the roll and the wrist barely moving.
        public const float MaxRollRateDegS = 120f, MaxWristSpeedMS = 0.35f;
        /// The fingers must point roughly sideways / forward (a hand pointing at the floor or the sky has no roll).
        public const float MaxFingerPitch = 0.8f;

        public enum Thumb { None, Up, Down }

        /// The hand's roll (degrees) from its palm frame, and whether the frame is usable (fingers not vertical).
        public static float Roll(bool left, Vector3 wrist, Vector3 middleKnuckle, Vector3 indexKnuckle, Vector3 littleKnuckle, out bool usable)
        {
            Vector3 f = middleKnuckle - wrist;
            usable = f.sqrMagnitude > 1e-6f;
            if (!usable) return 0f;
            f.Normalize();
            usable = Mathf.Abs(Vector3.Dot(f, Vector3.up)) <= MaxFingerPitch;
            Vector3 s = Vector3.ProjectOnPlane(indexKnuckle - littleKnuckle, f);
            if (s.sqrMagnitude < 1e-8f) { usable = false; return 0f; }
            s.Normalize();
            // The back of the hand: a right hand palm-down with the fingers ahead has its thumb on the left.
            Vector3 dorsal = left ? Vector3.Cross(f, s) : Vector3.Cross(s, f);
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, f);
            if (up.sqrMagnitude < 1e-8f) { usable = false; return 0f; }
            up.Normalize();
            return Mathf.Atan2(Vector3.Dot(s, up), Vector3.Dot(dorsal, up)) * Mathf.Rad2Deg;
        }

        public static Thumb Classify(float rollDeg)
        {
            if (rollDeg >= RollMinDeg && rollDeg <= RollMaxDeg) return Thumb.Up;
            if (rollDeg <= -RollMinDeg && rollDeg >= -RollMaxDeg) return Thumb.Down;
            return Thumb.None;
        }

        /// Raised in front of this player: ahead of the face, within reach, above the waist.
        public static bool Raised(Vector3 head, Vector3 headForward, Vector3 wrist)
        {
            Vector3 fwd = headForward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) return false;
            fwd.Normalize();
            Vector3 to = wrist - head;
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            return Vector3.Dot(flat, fwd) >= MinAheadM && to.magnitude <= MaxReachM && wrist.y >= head.y - AboveWaistBelowEyesM;
        }

        /// One hand's running state.
        public struct State
        {
            public Thumb pose;
            public float heldFor, lastRoll;
            public Vector3 lastWrist;
            public bool hasLast, fired;
        }

        /// One frame for one hand. Returns Up / Down once, when the same steady pose has been held for HoldSeconds;
        /// it re-arms only after the hand leaves the pose.
        public static Thumb Step(ref State st, bool tracked, bool raised, float roll, bool usable, Vector3 wrist, float dt)
        {
            Thumb now = tracked && raised && usable ? Classify(roll) : Thumb.None;
            bool steady = true;
            if (st.hasLast && dt > 0f)
            {
                float rate = Mathf.Abs(Mathf.DeltaAngle(st.lastRoll, roll)) / dt;
                float speed = (wrist - st.lastWrist).magnitude / dt;
                steady = rate <= MaxRollRateDegS && speed <= MaxWristSpeedMS;
            }
            st.lastRoll = roll; st.lastWrist = wrist; st.hasLast = tracked;
            if (now != st.pose) { st.pose = now; st.heldFor = 0f; st.fired = false; }
            if (now == Thumb.None) return Thumb.None;
            if (!steady) { st.heldFor = 0f; return Thumb.None; }
            st.heldFor += dt;
            if (!st.fired && st.heldFor >= HoldSeconds) { st.fired = true; return now; }
            return Thumb.None;
        }
    }

    /// <summary>The live detector: both tracked hands (<see cref="MagicHands"/>) against the viewer's head.</summary>
    public sealed class MagicThumbGesture
    {
        MagicThumbRules.State _l, _r;

        /// For the Test Mode overlay: each hand's last roll, pose, hold progress, and whether it counted as raised.
        public float LeftRoll { get; private set; }
        public float RightRoll { get; private set; }
        public string Debug { get; private set; } = "";

        public MagicThumbRules.Thumb Tick(float dt)
        {
            Camera cam = Camera.main;
            if (cam == null) return MagicThumbRules.Thumb.None;
            IReadOnlyList<MagicHands.Hand> hands = MagicHands.Now();
            bool seenL = false, seenR = false;
            var result = MagicThumbRules.Thumb.None;
            var sb = new System.Text.StringBuilder();
            foreach (MagicHands.Hand h in hands)
            {
                if (!h.valid) continue;
                float roll = MagicThumbRules.Roll(h.left, h.wrist, h.middleKnuckle, h.indexKnuckle, h.littleKnuckle, out bool usable);
                bool raised = MagicThumbRules.Raised(cam.transform.position, cam.transform.forward, h.wrist);
                MagicThumbRules.Thumb t;
                if (h.left) { seenL = true; LeftRoll = roll; t = MagicThumbRules.Step(ref _l, true, raised, roll, usable, h.wrist, dt); sb.Append(Line("L", roll, raised, usable, _l)); }
                else { seenR = true; RightRoll = roll; t = MagicThumbRules.Step(ref _r, true, raised, roll, usable, h.wrist, dt); sb.Append(Line("R", roll, raised, usable, _r)); }
                if (t != MagicThumbRules.Thumb.None) result = t;
            }
            if (!seenL) { MagicThumbRules.Step(ref _l, false, false, 0f, false, Vector3.zero, dt); sb.Append("L  —\n"); }
            if (!seenR) { MagicThumbRules.Step(ref _r, false, false, 0f, false, Vector3.zero, dt); sb.Append("R  —\n"); }
            Debug = sb.ToString();
            return result;
        }

        static string Line(string side, float roll, bool raised, bool usable, MagicThumbRules.State st)
            => $"{side} roll {roll:+0;-0}°  {(st.pose == MagicThumbRules.Thumb.Up ? "UP" : st.pose == MagicThumbRules.Thumb.Down ? "DOWN" : "—")}"
               + $"  {Mathf.Min(1f, st.heldFor / MagicThumbRules.HoldSeconds) * 100f:0}%{(raised ? "" : "  (not raised)")}{(usable ? "" : "  (fingers vertical)")}\n";

        /// The thumb glyph (up; rotate the plate 180° for down): Material Symbols Rounded `thumb_up` filled (Apache 2.0),
        /// the closest open-licensed match to the app's SF `hand.thumbsup.fill`; a drawn fist only if it is missing.
        public static Texture2D ThumbTexture() => MagicLook.Glyph("MagicThumbUp") ?? DrawnThumb();

        static Texture2D DrawnThumb() => MagicLook.Tex("ThumbUp", 128, 128, (u, v) =>
        {
            float Box(float cx, float cy, float hx, float hy, float r)
            {
                float dx = Mathf.Max(0f, Mathf.Abs(u - cx) - (hx - r)), dy = Mathf.Max(0f, Mathf.Abs(v - cy) - (hy - r));
                return Mathf.Sqrt(dx * dx + dy * dy) - r;
            }
            float Capsule(float ax, float ay, float bx, float by, float r)
            {
                Vector2 p = new Vector2(u, v), a = new Vector2(ax, ay), b = new Vector2(bx, by);
                float t = Mathf.Clamp01(Vector2.Dot(p - a, b - a) / (b - a).sqrMagnitude);
                return (p - (a + (b - a) * t)).magnitude - r;
            }
            float fist = Box(0.56f, 0.36f, 0.24f, 0.2f, 0.07f);
            float thumb = Capsule(0.42f, 0.5f, 0.5f, 0.86f, 0.08f);
            float cuff = Box(0.22f, 0.36f, 0.06f, 0.21f, 0.03f);
            float d = Mathf.Min(Mathf.Min(fist, thumb), cuff);
            // A thin gap between the fingers' knuckle lines reads as a fist.
            float lines = 0f;
            for (int k = 1; k < 4; k++) lines = Mathf.Max(lines, Mathf.Clamp01(1f - Mathf.Abs(v - (0.16f + k * 0.1f)) / 0.008f) * (u > 0.5f && u < 0.79f ? 1f : 0f));
            return Mathf.Clamp01(-d / 0.012f) * (1f - 0.6f * lines);
        });
    }
}
