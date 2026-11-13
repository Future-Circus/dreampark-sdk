using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// The pure maths behind <see cref="FeetTracker"/> (Oct 27 2026). There is no real foot tracking on Quest, so the
    /// foot is ESTIMATED from the head and kicks are PREDICTED. Tuned for feel, not accuracy (Aidan: "just like
    /// Wiimotes and Wii Sports"): a player who clearly means to kick gets a kick; someone walking past or standing still
    /// does not.
    ///
    /// Everything is driven by TIMESTAMPED head samples, never by frame or physics-step counts, so the result is the
    /// same at 36, 50, 72, 90 or 120 fps. No scene, no physics: the editor harness (Tools/Builders/KickHarness.cs)
    /// replays head motion through this class directly.
    ///
    /// Model:
    ///   foot   = the floor point under the head, pushed restReach forward along where you face, plus a momentum lead
    ///            along where you are going (speed x leadTime, capped at maxLead).
    ///   strike = a quick sweep of the foot from there INTO a kickable, started when a kickable is in front of you,
    ///            within reach, you are looking at it, and your head shows intent (a jolt, or a lean / step toward it).
    ///            The sweep speed scales with how emphatic that motion was (power = emphasis).
    /// </summary>
    public sealed class KickPredictor {
        [Serializable]
        public struct Settings {
            public float restReach;        // m, foot ahead of the body's floor point when standing
            public float neckForward;      // m, eyes sit this far in front of the neck pivot (head model)
            public float neckUp;           // m, and this far above it
            public float leadTime;         // s, how far ahead the momentum lead looks
            public float maxLead;          // m
            public float momentumWindow;   // s
            public float nowWindow;        // s
            public float fallbackDrop;     // m below the head when no floor is known (old yOffset)
            // strike (standing / emphatic kick)
            public float strikeMinReach;   // m, target surface nearer than this is already under the foot
            public float strikeMaxReach;   // m, farthest target surface a strike may go for (from the head's floor point)
            public float facingCone;       // deg, target must be within this of where you face
            public float gazeCone;         // deg, and within this of where you look
            public float joltThreshold;    // m/s, change of head velocity over joltWindow that counts as intent
            public float joltWindow;       // s
            public float leanDistance;     // m, head moved this far toward the target within leanWindow
            public float leanWindow;       // s
            public float walkPastSpeed;    // m/s, faster than this while NOT walking at the target = no strike
            public float walkPastDot;      // walking: direction of travel · direction to the target must be ≥ this
            public float gazeDwell;        // s, the target must have been looked at this long before a kick counts
            public float maxTurnRate;      // deg/s, head turning faster than this = looking around, not kicking
            public float strikeSpeedMin;   // m/s, sweep speed of a gentle kick
            public float strikeSpeedMax;   // m/s, sweep speed of an emphatic kick
            public float strikeHold;       // s, foot stays in the target
            public float strikeReturn;     // s
            public float strikeCooldown;   // s after a strike ends
            public float strikeOvershoot;  // fraction of the target radius the foot goes past its centre-facing surface

            public static Settings Default => new Settings {
                restReach = 0.10f, neckForward = 0.08f, neckUp = 0.075f, leadTime = 0.30f, maxLead = 0.35f, momentumWindow = 0.35f, nowWindow = 0.10f,
                fallbackDrop = 1.6f,
                strikeMinReach = 0.0f, strikeMaxReach = 0.80f, facingCone = 50f, gazeCone = 38f,
                joltThreshold = 0.20f, joltWindow = 0.12f, leanDistance = 0.04f, leanWindow = 0.22f,
                walkPastSpeed = 0.45f, walkPastDot = 0.55f, gazeDwell = 0.2f, maxTurnRate = 110f,
                strikeSpeedMin = 2.2f, strikeSpeedMax = 5.5f, strikeHold = 0.10f, strikeReturn = 0.18f,
                strikeCooldown = 0.45f, strikeOvershoot = 0.6f,
            };
        }

        /// <summary>A kickable the predictor may aim a strike at. <c>position</c> = its centre; <c>radius</c> = horizontal size.</summary>
        public struct Target {
            public Vector3 position;
            public float radius;
            public int id;           // caller's id (instance id / key hash); echoed in StrikeInfo
            public Target(Vector3 position, float radius, int id) { this.position = position; this.radius = radius; this.id = id; }
        }

        public enum Source { Estimated = 0, Predicted = 1, Requested = 2 }

        public struct FootState {
            public Vector3 position;     // floor point under the foot (y = floor)
            public Vector3 velocity;     // m/s, time-based
            public Vector3 direction;    // unit, horizontal
            public float floorY;
            public bool floorMeasured;
            public bool striking;
            public Source source;
            public float strikeConfidence; // of the strike running now (0 when not striking)
            public string strikeSource;    // "predicted" or the requester's label
            public int strikeTargetId;
            public double time;
        }

        // pos = the eyes (what the headset reports); body = the neck pivot under them. Every MOTION signal uses body,
        // so turning or nodding the head (eyes swinging around the neck) never reads as a lean or a kick.
        struct Sample { public double t; public Vector3 pos, body, fwd, up; }

        struct Strike {
            public bool active;
            public double t0, tHit, tHoldEnd, tEnd;
            public Vector3 from, to;      // floor points (y ignored; floor added on sample)
            public Vector3 dir;
            public float speed;
            public float confidence;
            public string source;
            public int targetId;
        }

        public Settings settings = Settings.Default;
        public float kickMultiplier = 1f;   // scales the momentum lead (FeetTracker.kickMultiplier)

        readonly Sample[] _ring = new Sample[512];
        int _head = -1, _count;
        float? _floor;
        Strike _strike;
        double _lastStrikeEnd = double.NegativeInfinity;
        double _lastStep = double.NegativeInfinity;
        readonly List<Target> _targets = new List<Target>(16);

        /// <summary>The last strike that STARTED (for logs / events). </summary>
        public int StrikesStarted { get; private set; }
        public string LastStrikeReason { get; private set; } = "";
        /// <summary>Intent signals for the best eligible target on the last Step (diagnostics / tuning).</summary>
        public float LastJolt { get; private set; }
        public float LastLean { get; private set; }

        // ── Inputs ────────────────────────────────────────────────────────────
        public void AddHeadSample(double t, Vector3 headPos, Vector3 headForward, Vector3 headUp) {
            if (_count > 0 && t <= _ring[_head].t) {
                // same timestamp: replace (a second call in the same frame)
                if (t == _ring[_head].t) { _ring[_head] = MakeSample(t, headPos, headForward, headUp); }
                return;
            }
            // a teleport / relocalization (the head jumped > 0.6 m within 0.2 s) is not motion: start the history over,
            // or the momentum lead would throw the foot 35 cm along the jump
            if (_count > 0 && t - _ring[_head].t < 0.2 && (headPos - _ring[_head].pos).sqrMagnitude > 0.36f) {
                _count = 0; _head = -1; _strike.active = false;
            }
            _head = (_head + 1) % _ring.Length;
            _ring[_head] = MakeSample(t, headPos, headForward, headUp);
            if (_count < _ring.Length) _count++;
        }

        Sample MakeSample(double t, Vector3 pos, Vector3 fwd, Vector3 up) {
            return new Sample { t = t, pos = pos, fwd = fwd, up = up,
                                body = pos - fwd * settings.neckForward - up * settings.neckUp };
        }

        /// <summary>Measured floor height under the player, or null (then head − fallbackDrop is used).</summary>
        public void SetFloor(float? floorY) { _floor = floorY; }

        public void SetTargets(IList<Target> targets) {
            _targets.Clear();
            if (targets != null) for (int i = 0; i < targets.Count; i++) _targets.Add(targets[i]);
        }

        public void ClearHistory() { _count = 0; _head = -1; _strike.active = false; _lastStep = double.NegativeInfinity; }

        public bool HasSamples => _count > 0;
        public double LastSampleTime => _count > 0 ? _ring[_head].t : 0;

        /// <summary>
        /// Start a strike toward <paramref name="target"/> now (titles, the depth probe). Returns false while one runs.
        /// <paramref name="emphasis"/> 0..1 sets the sweep speed.
        /// </summary>
        public bool RequestStrike(double t, Vector3 target, float targetRadius, float confidence, string source, float emphasis = 0.6f, int targetId = 0) {
            if (_count == 0 || (_strike.active && t < _strike.tEnd)) return false;
            Sample h = HeadAt(t);
            StartStrike(t, h, target, targetRadius, emphasis, confidence, string.IsNullOrEmpty(source) ? "requested" : source, targetId);
            LastStrikeReason = "requested:" + _strike.source;
            return true;
        }

        // ── Stepping ─────────────────────────────────────────────────────────
        /// <summary>
        /// Advance the strike state machine to time <paramref name="t"/> (call once per frame after AddHeadSample).
        /// Returns true when a predicted strike started on this step.
        /// </summary>
        public bool Step(double t) {
            if (_count == 0) return false;
            if (t <= _lastStep) return false;
            _lastStep = t;
            if (_strike.active && t >= _strike.tEnd) { _strike.active = false; _lastStrikeEnd = _strike.tEnd; }
            if (_strike.active) return false;
            if (t - _lastStrikeEnd < settings.strikeCooldown) return false;
            if (_targets.Count == 0) return false;

            Sample h = HeadAt(t);
            Vector3 headXZ = Flat(h.body);
            Vector3 facing = Facing(h.fwd, h.up);
            Vector3 vMom = HorizontalVelocity(t, settings.momentumWindow);
            // turning the head fast = looking around (the eyes swing around the neck and read as motion): no kick
            float turnRate = Vector3.Angle(HeadAt(t - 0.1).fwd, h.fwd) / 0.1f;
            if (turnRate > settings.maxTurnRate) return false;

            float bestScore = float.MaxValue; int best = -1; float bestSurf = 0;
            for (int i = 0; i < _targets.Count; i++) {
                Target tg = _targets[i];
                Vector3 to = Flat(tg.position) - headXZ;
                float d = to.magnitude;
                float surf = d - tg.radius;
                if (surf > settings.strikeMaxReach || surf < settings.strikeMinReach - tg.radius) continue;
                Vector3 toN = d > 1e-4f ? to / d : facing;
                if (Vector3.Angle(facing, toN) > settings.facingCone) continue;
                // gaze: the ray from the eye toward the target's centre vs where the head points
                Vector3 eyeTo = tg.position - h.pos;
                if (eyeTo.sqrMagnitude > 1e-6f && Vector3.Angle(h.fwd, eyeTo) > settings.gazeCone) continue;
                // ...and has been looked at for gazeDwell already (turning to look at it is not a kick)
                bool dwelt = true;
                for (float back = 0.1f; back <= settings.gazeDwell + 1e-4f; back += 0.1f) {
                    Sample hb = HeadAt(t - back);
                    if (Vector3.Angle(hb.fwd, tg.position - hb.pos) > settings.gazeCone) { dwelt = false; break; }
                }
                if (!dwelt) continue;
                // walking past: only a walk AT the target can turn into a kick
                float sp = vMom.magnitude;
                if (sp > settings.walkPastSpeed && Vector3.Dot(vMom / sp, toN) < settings.walkPastDot) continue;
                if (surf < bestScore) { bestScore = surf; best = i; bestSurf = surf; }
            }
            if (best < 0) return false;

            Target target = _targets[best];
            Vector3 toT = Flat(target.position) - headXZ;
            Vector3 dirT = toT.sqrMagnitude > 1e-8f ? toT.normalized : facing;

            // intent: a jolt (head velocity changed sharply) or a lean / step toward the target
            Vector3 vA = Velocity3(t, settings.joltWindow);
            Vector3 vB = Velocity3(t - settings.joltWindow, settings.joltWindow);
            float jolt = (vA - vB).magnitude;
            Vector3 lean = Flat(h.body) - Flat(HeadAt(t - settings.leanWindow).body);
            float leanToward = Vector3.Dot(lean, dirT);
            LastJolt = jolt; LastLean = leanToward;
            bool hasHistory = (t - OldestTime()) >= settings.leanWindow * 0.9;
            if (!hasHistory) return false;
            bool joltOk = jolt >= settings.joltThreshold;
            bool leanOk = leanToward >= settings.leanDistance;
            if (!joltOk && !leanOk) return false;

            // power = emphasis: how far past the intent thresholds the motion went (0.25 = a gentle tap, 1 = a big kick)
            float emphasis = Mathf.Clamp01(0.25f + 0.75f * Mathf.Max(
                (jolt - settings.joltThreshold) / 0.35f,
                (leanToward - settings.leanDistance) / 0.15f));
            float confidence = 0.7f;
            StartStrike(t, h, target.position, target.radius, emphasis, confidence, "predicted", target.id);
            LastStrikeReason = string.Format("predicted jolt={0:0.00} lean={1:0.000} surf={2:0.00} emph={3:0.00}", jolt, leanToward, bestSurf, emphasis);
            return true;
        }

        void StartStrike(double t, Sample h, Vector3 target, float radius, float emphasis, float confidence, string source, int targetId) {
            Vector3 headXZ = Flat(h.body);
            Vector3 from = BaseFoot(t, h);
            Vector3 tXZ = Flat(target);
            Vector3 dir = tXZ - from;
            if (dir.sqrMagnitude < 1e-6f) dir = Facing(h.fwd, h.up);
            dir.Normalize();
            // aim at the target's centre, going a little past its near surface so the foot really touches
            float dist = (tXZ - from).magnitude;
            float travel = Mathf.Max(0.05f, dist - radius + radius * settings.strikeOvershoot);
            Vector3 to = from + dir * travel;
            // never reach further than strikeMaxReach + radius from the head's floor point
            Vector3 fromHead = to - headXZ;
            float cap = settings.strikeMaxReach + radius;
            if (fromHead.magnitude > cap) to = headXZ + fromHead.normalized * cap;
            float speed = Mathf.Lerp(settings.strikeSpeedMin, settings.strikeSpeedMax, Mathf.Clamp01(emphasis));
            float dur = Mathf.Clamp((to - from).magnitude / speed, 0.06f, 0.30f);
            _strike = new Strike {
                active = true, t0 = t, tHit = t + dur, tHoldEnd = t + dur + settings.strikeHold,
                tEnd = t + dur + settings.strikeHold + settings.strikeReturn,
                from = from, to = to, dir = dir, speed = (to - from).magnitude / dur,
                confidence = confidence, source = source, targetId = targetId,
            };
            StrikesStarted++;
        }

        // ── Output ───────────────────────────────────────────────────────────
        /// <summary>The foot at time <paramref name="t"/> (no state change). Velocity is a finite difference over 60 ms.</summary>
        public FootState Evaluate(double t) {
            var s = new FootState { time = t };
            if (_count == 0) { s.direction = Vector3.forward; return s; }
            Vector3 p1 = FootAt(t, out bool striking, out Vector3 dir);
            const double dt = 0.06;
            Vector3 p0 = FootAt(t - dt, out _, out _);
            Sample h = HeadAt(t);
            float floor = _floor ?? (h.pos.y - settings.fallbackDrop);
            s.floorY = floor;
            s.floorMeasured = _floor.HasValue;
            s.position = new Vector3(p1.x, floor, p1.z);
            s.velocity = (p1 - p0) / (float)dt;
            s.velocity.y = 0;
            s.direction = dir;
            s.striking = striking;
            s.source = striking ? (_strike.source == "predicted" ? Source.Predicted : Source.Requested) : Source.Estimated;
            s.strikeConfidence = striking ? _strike.confidence : 0f;
            s.strikeSource = striking ? _strike.source : null;
            s.strikeTargetId = striking ? _strike.targetId : 0;
            return s;
        }

        /// <summary>Speed of the strike running at time t, m/s (0 = none). For "power from emphasis".</summary>
        public float StrikeSpeedAt(double t) => (_strike.active && t >= _strike.t0 && t <= _strike.tHoldEnd) ? _strike.speed : 0f;

        Vector3 FootAt(double t, out bool striking, out Vector3 dir) {
            Sample h = HeadAt(t);
            Vector3 basePos = BaseFoot(t, h);
            Vector3 vMom = HorizontalVelocity(t, settings.momentumWindow);
            dir = vMom.magnitude > 0.15f ? vMom.normalized : Facing(h.fwd, h.up);
            striking = false;
            if (_strike.active && t >= _strike.t0 && t < _strike.tEnd) {
                striking = true;
                dir = _strike.dir;
                if (t < _strike.tHit) {
                    float u = (float)((t - _strike.t0) / (_strike.tHit - _strike.t0));
                    // fast in, a touch of ease-out at the end
                    float e = 1f - (1f - u) * (1f - u) * 0.35f - (1f - u) * 0.65f;
                    return Vector3.LerpUnclamped(_strike.from, _strike.to, Mathf.Clamp01(e));
                }
                if (t < _strike.tHoldEnd) return _strike.to;
                float r = (float)((t - _strike.tHoldEnd) / (_strike.tEnd - _strike.tHoldEnd));
                return Vector3.Lerp(_strike.to, basePos, Mathf.SmoothStep(0f, 1f, r));
            }
            return basePos;
        }

        Vector3 BaseFoot(double t, Sample h) {
            Vector3 facing = Facing(h.fwd, h.up);
            Vector3 p = Flat(h.body) + facing * settings.restReach;
            Vector3 vMom = HorizontalVelocity(t, settings.momentumWindow);
            float sp = vMom.magnitude;
            if (sp > 1e-4f) p += (vMom / sp) * Mathf.Min(settings.maxLead, sp * settings.leadTime) * Mathf.Max(0f, kickMultiplier);
            return p;
        }

        // ── History helpers ──────────────────────────────────────────────────
        double OldestTime() {
            int oldest = (_head - _count + 1 + _ring.Length) % _ring.Length;
            return _ring[oldest].t;
        }

        Sample HeadAt(double t) {
            Sample last = _ring[_head];
            if (t >= last.t || _count == 1) return last;
            // walk back
            for (int k = 0; k < _count - 1; k++) {
                int i = (_head - k + _ring.Length) % _ring.Length;
                int j = (i - 1 + _ring.Length) % _ring.Length;
                Sample a = _ring[j], b = _ring[i];
                if (t >= a.t) {
                    float u = (float)((t - a.t) / Math.Max(1e-9, b.t - a.t));
                    return new Sample {
                        t = t, pos = Vector3.LerpUnclamped(a.pos, b.pos, u), body = Vector3.LerpUnclamped(a.body, b.body, u),
                        // normalized lerp, not Slerp: Vector3.Slerp is an engine call, and this class must run outside Unity
                        fwd = Vector3.LerpUnclamped(a.fwd, b.fwd, u).normalized, up = Vector3.LerpUnclamped(a.up, b.up, u).normalized,
                    };
                }
            }
            int oldest = (_head - _count + 1 + _ring.Length) % _ring.Length;
            return _ring[oldest];
        }

        Vector3 Velocity3(double t, float window) {
            double t0 = Math.Max(t - window, OldestTime());
            if (t - t0 < 1e-3) return Vector3.zero;
            return (HeadAt(t).body - HeadAt(t0).body) / (float)(t - t0);
        }

        Vector3 HorizontalVelocity(double t, float window) { Vector3 v = Velocity3(t, window); v.y = 0; return v; }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>Where you face on the floor: forward + up projected (works looking level or straight down).</summary>
        public static Vector3 Facing(Vector3 fwd, Vector3 up) {
            Vector3 f = Flat(fwd + up * 0.5f);
            if (f.sqrMagnitude < 1e-6f) f = Flat(fwd);
            if (f.sqrMagnitude < 1e-6f) return Vector3.forward;
            return f.normalized;
        }

        /// <summary>
        /// Swept circle vs circle in XZ: the foot (radius footR) moving a→b against a static target at c (radius r).
        /// Returns the hit fraction 0..1, or −1. Frame-rate independent contact for virtual targets.
        /// </summary>
        public static float SweptHit(Vector3 a, Vector3 b, Vector3 c, float footR, float r) {
            float R = footR + r;
            float dx = a.x - c.x, dz = a.z - c.z;
            float ex = b.x - a.x, ez = b.z - a.z;
            float cc = dx * dx + dz * dz - R * R;
            if (cc <= 0f) return 0f;
            float aa = ex * ex + ez * ez;
            if (aa < 1e-12f) return -1f;
            float bb = 2f * (dx * ex + dz * ez);
            float disc = bb * bb - 4f * aa * cc;
            if (disc < 0f) return -1f;
            float f = (-bb - Mathf.Sqrt(disc)) / (2f * aa);
            return (f >= 0f && f <= 1f) ? f : -1f;
        }
    }
}
