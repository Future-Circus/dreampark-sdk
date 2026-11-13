using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DreamPark;
using UnityEngine;

/// <summary>
/// KICK HARNESS (Oct 27 2026): replays head motion through the v1 foot (the old FeetTracker, re-implemented here
/// verbatim) and the v2 foot (<see cref="KickPredictor"/> driven exactly the way FeetTracker v2 drives it) at several
/// frame rates, with physics at 50 Hz, and reports whether each foot touches a SAL Jubbo (capsule r 0.30 m,
/// 0.025–0.575 m above the floor). Pure: no scene, no Play mode, managed maths only, so it also runs outside Unity.
///
///   Editor:  Tools/agent/u.sh run_script --file Tools/Builders/KickHarness.cs --entry KickHarness.Run
///   Offline: WC_TAG=kick Tools/wholecompile/wc.sh E && WC_TAG=kick Tools/wholecompile/kick-harness.sh [outDir]
///
/// The head traces are SYNTHETIC (no recorded Quest traces exist yet): a standing kick = the small head shift a
/// kick makes (back + up, then forward), modelled from video of people kicking; walking = 1 m/s with a 2 Hz bob.
/// `outDir` (optional) gets one CSV per scenario per fps (time, head, v1 foot, v2 foot, contacts) for plots.
/// </summary>
public static class KickHarness
{
    const float JubboR = 0.30f, JubboLo = 0.025f, JubboHi = 0.575f;
    static readonly Vector3 FootSize = new Vector3(0.25f, 0.6f, 0.30f);
    const float FixedDt = 0.02f;

    struct Pose { public Vector3 pos, fwd, up; }

    class Scenario {
        public string name, note;
        public float eye = 1.62f;
        public Vector3 jubbo;            // floor point under the Jubbo
        public float duration;
        public bool shouldKick;
        public Func<float, Scenario, Pose> head;
    }

    static int s_pass, s_fail;
    static StringBuilder s_sb;

    // ── Head helpers ─────────────────────────────────────────────────────
    static Pose LookAt(Vector3 eye, Vector3 at)
    {
        Vector3 f = (at - eye).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, f);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
        right.Normalize();
        return new Pose { pos = eye, fwd = f, up = Vector3.Cross(f, right).normalized };
    }
    static Pose LookDir(Vector3 eye, Vector3 dir, float pitchDownDeg)
    {
        float p = pitchDownDeg * Mathf.Deg2Rad;
        Vector3 d = new Vector3(dir.x, 0, dir.z).normalized;
        return LookAt(eye, eye + d * Mathf.Cos(p) - Vector3.up * Mathf.Sin(p));
    }
    static Vector3 JubboCenter(Scenario s) => s.jubbo + Vector3.up * 0.30f;

    // a standing kick: 0.12 s back 3 cm + up 1.5 cm (wind-up), 0.18 s forward 6 cm (follow-through), 0.4 s settle
    static Vector3 KickShift(float u, Vector3 toward, float scale)
    {
        Vector3 back = -toward * 0.03f + Vector3.up * 0.015f;
        Vector3 fwd = toward * 0.06f - Vector3.up * 0.01f;
        if (u < 0) return Vector3.zero;
        if (u < 0.12f) return back * Smooth(u / 0.12f) * scale;
        if (u < 0.30f) return Vector3.Lerp(back, fwd, Smooth((u - 0.12f) / 0.18f)) * scale;
        if (u < 0.70f) return Vector3.Lerp(fwd, Vector3.zero, Smooth((u - 0.30f) / 0.40f)) * scale;
        return Vector3.zero;
    }
    static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
    static float Sway(float t, float seed) => 0.012f * Mathf.Sin(t * 1.9f + seed) + 0.004f * Mathf.Sin(t * 7.3f + seed * 2f);

    // turn the head (around the neck) from looking along `from` (pitch p0) to looking straight at the Jubbo
    static Pose TurnToJubbo(float t, Scenario s, Vector3 from, float p0, float t0, float dur)
    {
        Vector3 neck = new Vector3(0, s.eye - 0.075f, 0);
        float u = Smooth((t - t0) / dur);
        Pose a = LookDir(Vector3.zero, from, p0);
        Vector3 approxEye = neck + Vector3.up * 0.075f;
        Vector3 fB = (JubboCenter(s) - approxEye).normalized;
        Pose o = LookAt(Vector3.zero, Vector3.LerpUnclamped(a.fwd, fB, u).normalized);
        // a real head is not the predictor's model: 10.5 cm forward / 5.5 cm up (model 8 / 7.5), so a turn leaks motion
        o.pos = neck + o.fwd * 0.105f + o.up * 0.055f;
        return o;
    }

    static List<Scenario> Scenarios()
    {
        var list = new List<Scenario>();
        Func<float, float, Scenario> standingKick = (dist, scale) => new Scenario {
            name = $"standing_kick_{dist:0.0}m" + (scale > 1.2f ? "_big" : scale < 0.8f ? "_small" : ""),
            note = $"stand, look at a Jubbo {dist:0.0} m ahead, kick at 1.0 s without stepping",
            jubbo = new Vector3(0, 0, dist), duration = 2.2f, shouldKick = true,
            head = (t, s) => {
                Vector3 eye = new Vector3(Sway(t, 1) * 0.5f, s.eye, Sway(t, 2) * 0.5f) + KickShift(t - 1.0f, Vector3.forward, scale);
                return LookAt(eye, JubboCenter(s));
            },
        };
        list.Add(standingKick(0.6f, 1f));
        list.Add(standingKick(0.75f, 1f));
        list.Add(standingKick(0.6f, 0.7f));
        list.Add(standingKick(0.6f, 1.6f));
        list.Add(new Scenario {
            name = "kid_standing_kick_0.7m", note = "a child (eyes 1.10 m) kicks a Jubbo 0.7 m ahead", eye = 1.10f,
            jubbo = new Vector3(0, 0, 0.7f), duration = 2.2f, shouldKick = true,
            head = (t, s) => LookAt(new Vector3(0, s.eye, 0) + KickShift(t - 1.0f, Vector3.forward, 0.8f), JubboCenter(s)),
        });
        list.Add(new Scenario {
            name = "walk_in_kick", note = "walk at 1 m/s toward a Jubbo 2.4 m away, looking at it, stop 0.55 m short",
            jubbo = new Vector3(0, 0, 2.4f), duration = 3.2f, shouldKick = true,
            head = (t, s) => {
                float z = Mathf.Min(t * 1.0f, 2.4f - 0.55f);
                float moving = z < 1.85f ? 1f : 0f;
                Vector3 eye = new Vector3(0, s.eye + 0.02f * Mathf.Sin(t * 12.6f) * moving, z);
                return LookAt(eye, JubboCenter(s));
            },
        });
        list.Add(new Scenario {
            name = "standing_still_looking", note = "stand 0.6 m from a Jubbo, look at it, natural sway, no kick",
            jubbo = new Vector3(0, 0, 0.6f), duration = 4f, shouldKick = false,
            head = (t, s) => LookAt(new Vector3(Sway(t, 3), s.eye + Sway(t, 5) * 0.3f, Sway(t, 4)), JubboCenter(s)),
        });
        list.Add(new Scenario {
            name = "walk_past", note = "walk past at 1 m/s, the Jubbo 0.9 m to the side, looking ahead",
            jubbo = new Vector3(0.9f, 0, 1.5f), duration = 3.2f, shouldKick = false,
            head = (t, s) => LookDir(new Vector3(0, s.eye + 0.02f * Mathf.Sin(t * 12.6f), t * 1.0f), Vector3.forward, 15f),
        });
        list.Add(new Scenario {
            name = "turn_to_look", note = "standing, turn 90° in 0.4 s to look at a Jubbo 0.8 m to the side, no kick",
            jubbo = new Vector3(0.8f, 0, 0), duration = 2.5f, shouldKick = false,
            head = (t, s) => TurnToJubbo(t, s, Vector3.forward, 10f, 0.8f, 0.4f),
        });
        list.Add(new Scenario {
            name = "nod_down_to_look", note = "standing, look down fast (0.3 s) at a Jubbo 0.7 m ahead, no kick",
            jubbo = new Vector3(0, 0, 0.7f), duration = 2.5f, shouldKick = false,
            head = (t, s) => TurnToJubbo(t, s, Vector3.forward, 0f, 0.8f, 0.3f),
        });
        return list;
    }

    // ── Contact: foot box (oriented in XZ) vs the Jubbo capsule ─────────
    static bool Touches(Vector3 footCenter, Vector3 footDir, Scenario s)
    {
        float lo = footCenter.y - FootSize.y * 0.5f, hi = footCenter.y + FootSize.y * 0.5f;
        if (hi < JubboLo || lo > JubboHi) return false;
        Vector3 d = new Vector3(footDir.x, 0, footDir.z);
        if (d.sqrMagnitude < 1e-6f) d = Vector3.forward;
        d.Normalize();
        Vector3 r = new Vector3(d.z, 0, -d.x);
        Vector3 rel = new Vector3(s.jubbo.x - footCenter.x, 0, s.jubbo.z - footCenter.z);
        float along = Mathf.Clamp(Vector3.Dot(rel, d), -FootSize.z * 0.5f, FootSize.z * 0.5f);
        float side = Mathf.Clamp(Vector3.Dot(rel, r), -FootSize.x * 0.5f, FootSize.x * 0.5f);
        Vector3 closest = d * along + r * side;
        return (rel - closest).magnitude <= JubboR;
    }

    // ── v1: the old FeetTracker, verbatim logic ─────────────────────────
    class LegacyFoot {
        readonly List<Vector3> samples = new List<Vector3>();
        Vector3 last; bool started;
        public Vector3 pos; public Vector3 dir = Vector3.forward;
        public const float YOffset = -1.3f;
        public void Fixed() {
            Vector3 xz = new Vector3(pos.x, 0, pos.z);
            samples.Add(xz - last); last = xz;
            if (samples.Count > 30) samples.RemoveAt(0);
        }
        Vector3 Sum() { Vector3 s = Vector3.zero; foreach (var v in samples) s += v; return s; }
        public Vector3 Direction() => samples.Count > 0 ? Sum().normalized : Vector3.forward;
        public float Velocity(float m = 1f) => samples.Count > 0 ? m * Sum().magnitude / samples.Count : 0f;
        public void Late(Vector3 head) {
            if (!started) { last = head; started = true; }   // Start(): lastPosition = head.position (not XZ, as v1)
            pos = new Vector3(head.x, head.y + YOffset, head.z) + Direction() * Mathf.Clamp(Velocity(10f), 0f, 0.2f);
            if (Velocity() > 0.01f) dir = Direction();
        }
    }

    struct Result { public bool v1, v2; public float v1At, v2At; public int strikes; public string reason; public float peakJolt, peakLean; }

    static Result RunOne(Scenario s, float fps, StringBuilder csv)
    {
        var legacy = new LegacyFoot();
        var pred = new KickPredictor();
        var settings = KickPredictor.Settings.Default;
        const float footCenterHeight = 0.3f;
        settings.fallbackDrop = footCenterHeight - LegacyFoot.YOffset;
        pred.settings = settings;
        var targets = new List<KickPredictor.Target> { new KickPredictor.Target(JubboCenter(s), JubboR, 1) };

        var res = new Result { v1At = -1, v2At = -1 };
        double frameDt = 1.0 / fps, t = 0, fixedT = 0;
        double smoothDt = frameDt;
        csv?.Append("t,head_x,head_y,head_z,v1_x,v1_y,v1_z,v1_hit,v2_x,v2_z,v2_floor,v2_strike,v2_hit\n");
        while (t <= s.duration)
        {
            // Unity order: the FixedUpdates due before this frame, then Update/LateUpdate.
            while (fixedT + FixedDt <= t + 1e-9)
            {
                fixedT += FixedDt;
                legacy.Fixed();
                bool h1 = Touches(legacy.pos, legacy.dir, s);
                if (h1 && !res.v1) { res.v1 = true; res.v1At = (float)fixedT; }
                if (pred.HasSamples)
                {
                    double te = Math.Min(fixedT, pred.LastSampleTime) - smoothDt;
                    var st = pred.Evaluate(te);
                    Vector3 c = st.position + Vector3.up * footCenterHeight;
                    bool h2 = Touches(c, st.direction, s);
                    if (h2 && !res.v2) { res.v2 = true; res.v2At = (float)fixedT; }
                    csv?.AppendFormat(CultureInfo.InvariantCulture, "{0:0.000},,,,{1:0.000},{2:0.000},{3:0.000},{4},{5:0.000},{6:0.000},{7:0.000},{8},{9}\n",
                        fixedT, legacy.pos.x, legacy.pos.y, legacy.pos.z, h1 ? 1 : 0, c.x, c.z, st.floorY, st.striking ? 1 : 0, h2 ? 1 : 0);
                }
            }
            Pose p = s.head((float)t, s);
            legacy.Late(p.pos);
            pred.SetFloor(0f);                     // the Level floor under the player (SAL keeps floor colliders)
            pred.SetTargets(targets);
            pred.AddHeadSample(t, p.pos, p.fwd, p.up);
            if (pred.Step(t)) { res.strikes++; res.reason = pred.LastStrikeReason; }
            if (t > 0.3) { res.peakJolt = Math.Max(res.peakJolt, pred.LastJolt); res.peakLean = Math.Max(res.peakLean, pred.LastLean); }
            csv?.AppendFormat(CultureInfo.InvariantCulture, "{0:0.000},{1:0.000},{2:0.000},{3:0.000}\n", t, p.pos.x, p.pos.y, p.pos.z);
            // frame-time jitter: ±15 % around the target fps
            double jitter = 1.0 + 0.15 * Math.Sin(t * 37.0);
            t += frameDt * jitter;
            smoothDt = smoothDt + (frameDt * jitter - smoothDt) * 0.2;
        }
        return res;
    }

    // ── Monte-Carlo sweep: many players, many kicks ─────────────────────
    // Each family draws its own random players (eye height 1.10–1.85 m, sway, tracking noise, distance, kick size,
    // walking speed, frame rate 36–120). Reports how often each foot kicks; v2 must kick ≥ 90% of clear kicks and
    // fire on ≤ 3% of the not-kicking cases.
    static Vector3 RotY(Vector3 v, float deg) { float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), n = Mathf.Sin(a); return new Vector3(v.x * c + v.z * n, v.y, -v.x * n + v.z * c); }
    static float R(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);

    static Scenario RandomScenario(string family, System.Random r)
    {
        float eye = R(r, 1.10f, 1.85f);
        float yaw = R(r, -180f, 180f) * Mathf.Deg2Rad;
        Vector3 dirTo = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
        Vector3 side = new Vector3(dirTo.z, 0, -dirTo.x);
        float sway = R(r, 0.002f, 0.015f), noise = 0.001f, seed = R(r, 0, 100);
        Func<float, Vector3> jitter = t => new Vector3(sway * Mathf.Sin(t * 1.9f + seed) + noise * Mathf.Sin(t * 53f + seed),
                                                       sway * 0.3f * Mathf.Sin(t * 2.7f + seed), sway * Mathf.Sin(t * 2.3f + seed * 2) + noise * Mathf.Sin(t * 61f));
        var s = new Scenario { name = family, eye = eye, duration = 2.6f };
        switch (family)
        {
            case "standing_kick": {
                float dist = R(r, 0.45f, 0.85f), scale = R(r, 0.6f, 1.6f), tk = R(r, 0.9f, 1.4f);
                s.jubbo = dirTo * dist; s.shouldKick = true;
                s.head = (t, sc) => LookAt(new Vector3(0, sc.eye, 0) + jitter(t) + KickShift(t - tk, dirTo, scale), JubboCenter(sc));
                break; }
            case "walk_in_kick": {
                float speed = R(r, 0.6f, 1.3f), stop = R(r, 0.45f, 0.65f), from = 2.2f;
                s.jubbo = dirTo * from; s.shouldKick = true; s.duration = 3.6f;
                s.head = (t, sc) => {
                    float d = Mathf.Min(t * speed, from - stop);
                    bool moving = d < from - stop;
                    return LookAt(new Vector3(0, sc.eye + (moving ? 0.02f * Mathf.Sin(t * 12.6f * speed) : 0f), 0) + dirTo * d + jitter(t), JubboCenter(sc));
                };
                break; }
            case "idle_near": {
                float dist = R(r, 0.5f, 0.9f), shift = R(r, 0f, 0.03f);
                s.jubbo = dirTo * dist; s.shouldKick = false; s.duration = 4f;
                s.head = (t, sc) => LookAt(new Vector3(0, sc.eye, 0) + jitter(t) + side * shift * Mathf.Sin(t * 0.9f + seed), JubboCenter(sc));
                break; }
            case "walk_past_looking": {
                float lateral = R(r, 0.8f, 1.2f), speed = R(r, 0.6f, 1.4f);
                s.jubbo = dirTo * 1.6f + side * lateral; s.shouldKick = false; s.duration = 3.4f;
                s.head = (t, sc) => LookAt(new Vector3(0, sc.eye + 0.02f * Mathf.Sin(t * 12.6f * speed), 0) + dirTo * (t * speed) + jitter(t), JubboCenter(sc));
                break; }
            case "turn_then_kick": {   // turn to a Jubbo beside you, look at it a moment, then kick
                float dist = R(r, 0.5f, 0.8f), dur = R(r, 0.3f, 0.6f), ang = R(r, 60f, 120f) * (r.NextDouble() < 0.5 ? -1 : 1);
                float wait = R(r, 0.35f, 0.9f), scale = R(r, 0.7f, 1.5f);
                Vector3 start = RotY(dirTo, -ang);
                s.jubbo = dirTo * dist; s.shouldKick = true; s.duration = 3.2f;
                s.head = (t, sc) => {
                    Pose o = TurnToJubbo(t, sc, start, 10f, 0.6f, dur);
                    o.pos += KickShift(t - (0.6f + dur + wait), dirTo, scale);
                    return o;
                };
                break; }
            default: { // turn_and_look: turn toward a Jubbo beside you, head model mismatched
                float dist = R(r, 0.6f, 1.0f), dur = R(r, 0.25f, 0.6f), ang = R(r, 60f, 150f) * (r.NextDouble() < 0.5 ? -1 : 1);
                Vector3 start = RotY(dirTo, -ang);
                s.jubbo = dirTo * dist; s.shouldKick = false;
                s.head = (t, sc) => TurnToJubbo(t, sc, start, R(new System.Random((int)seed), 0f, 20f), 0.8f, dur);
                break; }
        }
        return s;
    }

    static void Sweep(string outDir)
    {
        string[] fams = { "standing_kick", "walk_in_kick", "turn_then_kick", "idle_near", "walk_past_looking", "turn_and_look" };
        const int N = 150;
        var rnd = new System.Random(20261027);
        float[] rates = { 36f, 50f, 72f, 90f, 120f };
        s_sb.Append($"\nSweep: {N} random players per family (eyes 1.10–1.85 m, sway, distance, kick size, speed, 36–120 fps)\n");
        var csv = new StringBuilder("family,expect,v1_rate,v2_rate,n\n");
        foreach (var fam in fams)
        {
            int v1 = 0, v2 = 0; bool expect = false;
            for (int i = 0; i < N; i++)
            {
                var sc = RandomScenario(fam, rnd);
                expect = sc.shouldKick;
                var res = RunOne(sc, rates[rnd.Next(rates.Length)], null);
                if (res.v1) v1++; if (res.v2) v2++;
            }
            float r1 = 100f * v1 / N, r2 = 100f * v2 / N;
            s_sb.Append($"  {fam,-20} {(expect ? "should kick" : "should not"),-12} v1 {r1,5:0}%   v2 {r2,5:0}%\n");
            csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2:0.0},{3:0.0},{4}\n", fam, expect ? 1 : 0, r1, r2, N);
            if (expect) Check($"sweep {fam}: v2 kicks ≥ 90%", r2 >= 90f, $"{r2:0}%");
            else Check($"sweep {fam}: v2 false kicks ≤ 3%", r2 <= 3f, $"{r2:0}%");
        }
        if (!string.IsNullOrEmpty(outDir)) File.WriteAllText(Path.Combine(outDir, "sweep.csv"), csv.ToString());
    }

    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) s_pass++; else s_fail++;
        s_sb.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (!string.IsNullOrEmpty(detail)) s_sb.Append("  — ").Append(detail);
        s_sb.Append('\n');
    }

    public static string Run() => Run(null);

    public static string Run(string outDir)
    {
        s_pass = 0; s_fail = 0; s_sb = new StringBuilder();
        float[] rates = { 36f, 50f, 72f, 90f, 120f };
        s_sb.Append("KickHarness — v1 (old FeetTracker) vs v2 (KickPredictor), physics 50 Hz, ±15% frame jitter\n");
        s_sb.Append("scenario                       expect | fps:  ").Append(string.Join("   ", Array.ConvertAll(rates, r => r.ToString("0").PadLeft(3)))).Append('\n');
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        var summary = new StringBuilder("scenario,fps,expect,v1,v1_at,v2,v2_at,strikes\n");
        foreach (var s in Scenarios())
        {
            var row1 = new StringBuilder(); var row2 = new StringBuilder();
            foreach (float fps in rates)
            {
                StringBuilder csv = string.IsNullOrEmpty(outDir) ? null : new StringBuilder();
                Result r = RunOne(s, fps, csv);
                if (csv != null) File.WriteAllText(Path.Combine(outDir, $"{s.name}_{fps:0}fps.csv"), csv.ToString());
                summary.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:0},{2},{3},{4:0.00},{5},{6:0.00},{7}\n", s.name, fps, s.shouldKick ? 1 : 0, r.v1 ? 1 : 0, r.v1At, r.v2 ? 1 : 0, r.v2At, r.strikes);
                row1.Append(r.v1 ? "  KICK" : "  miss");
                row2.Append(r.v2 ? "  KICK" : "  miss");
                Check($"v2 {s.name} @{fps:0}fps", r.v2 == s.shouldKick,
                      (r.v2 ? $"contact {r.v2At:0.00}s" : "no contact") + (r.strikes > 0 ? $", {r.reason}" : $", peak jolt {r.peakJolt:0.00} lean {r.peakLean:0.000}"));
            }
            s_sb.Append((s.name + " v1").PadRight(31)).Append(s.shouldKick ? "kick  " : "none  ").Append('|').Append(row1).Append('\n');
            s_sb.Append((s.name + " v2").PadRight(31)).Append(s.shouldKick ? "kick  " : "none  ").Append('|').Append(row2).Append('\n');
        }
        if (!string.IsNullOrEmpty(outDir)) File.WriteAllText(Path.Combine(outDir, "summary.csv"), summary.ToString());

        Sweep(outDir);

        // Swept contact maths (virtual targets, e.g. a Lua ball).
        Check("SweptHit: crossing hits", KickPredictor.SweptHit(new Vector3(0, 0, -1), new Vector3(0, 0, 1), Vector3.zero, 0.1f, 0.1f) is float f1 && f1 > 0.3f && f1 < 0.5f);
        Check("SweptHit: miss to the side", KickPredictor.SweptHit(new Vector3(0.5f, 0, -1), new Vector3(0.5f, 0, 1), Vector3.zero, 0.1f, 0.1f) < 0f);
        Check("SweptHit: already inside = 0", KickPredictor.SweptHit(Vector3.zero, new Vector3(0, 0, 1), Vector3.zero, 0.1f, 0.1f) == 0f);
        Check("Facing: level and looking straight down agree",
              Vector3.Dot(KickPredictor.Facing(Vector3.forward, Vector3.up), KickPredictor.Facing(Vector3.down, Vector3.forward)) > 0.99f);

        s_sb.Append($"\n{s_pass} passed, {s_fail} failed\n");
        return s_sb.ToString();
    }
}
