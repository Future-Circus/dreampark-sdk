// ─────────────────────────────────────────────────────────────────────
//  DreamieMoments.cs — THE seam between the game and Dreamie's director
//  (repair-oct20 CONTRACT § 4; pl-runtime owns this file, dreamie-live consumes it).
//
//  Anything that wants Dreamie to react (a title's dp.dreamie.moment, a goal
//  block, the platform's own playlist moments) calls Moment / Say here, on
//  ANY device. Non-director devices forward to the director (the PlaylistRun
//  leader) through PlaylistRun's SharedState; the director's queue receives
//  everything, filters `once` / `cooldown`, orders by priority and raises
//  Raised. dreamie-live drains the queue (TryDequeue) or listens to Raised.
//
//  Platform moments (same path): playlist_intro, layout_appeared, guide_room,
//  stop_intro, progress_half, progress_last, stop_done, stop_locked,
//  boss_intro, stuck, idle, complete, results.
//
//  Pure C# (no Unity calls on the hot path): Log / Clock / DeviceId are
//  injectable so the offline multiplayer sim runs it outside Unity.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    /// One thing Dreamie may speak about. `kind` = "moment" (the director writes the words) or "say" (exact words).
    public sealed class DreamieMoment
    {
        public const string KindMoment = "moment", KindSay = "say";
        public const string Low = "low", Normal = "normal", High = "high";

        public string kind = KindMoment;
        /// [a-z0-9_] (moments). Says carry "say".
        public string name;
        /// JSON object text, ≤ 1 KB ("{}" when none).
        public string dataJson = "{}";
        /// The author's guidance for this beat (the playlist's dreamie beat `guide`, or the caller's).
        public string guide;
        public string priority = Normal;
        /// Where Dreamie should point / fly: an object on THIS device (not forwarded) and/or a world position.
        public GameObject pointObject;
        public bool hasPoint;
        public Vector3 point;
        /// Speak at most once per run for (name, stop).
        public bool once;
        /// Seconds before the same name may be accepted again (0 = none).
        public double cooldown;
        /// "say": the exact words + emotion.
        public string text, emotion;
        /// Filled by the platform: the device that raised it, the session clock, the run / stop / player it is about.
        public string from, runId, playlistId, stop, player;
        public double at;
        /// The playlist's word for its rounds ("Race" / "Races"; Round / Rounds by default) — Dreamie says it, never "round".
        public string roundOne, roundMany;

        public int PriorityRank => priority == High ? 2 : priority == Low ? 0 : 1;

        public DreamieMoment Clone() => (DreamieMoment)MemberwiseClone();

        public string ToJson()
        {
            var j = new JSONObject();
            j.AddField("k", kind ?? KindMoment);
            if (!string.IsNullOrEmpty(name)) j.AddField("n", name);
            var d = SharedState.ParseFragment(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
            j.AddField("d", d != null && d.type == JSONObject.Type.Object ? d : new JSONObject());
            if (!string.IsNullOrEmpty(guide)) j.AddField("g", guide);
            if (priority != Normal) j.AddField("p", priority ?? Normal);
            if (hasPoint)
            {
                var a = new JSONObject(JSONObject.Type.Array);
                a.Add(point.x); a.Add(point.y); a.Add(point.z);
                j.AddField("pt", a);
            }
            if (once) j.AddField("o", true);
            if (cooldown > 0) j.AddField("cd", cooldown);
            if (!string.IsNullOrEmpty(text)) j.AddField("tx", text);
            if (!string.IsNullOrEmpty(emotion)) j.AddField("em", emotion);
            if (!string.IsNullOrEmpty(from)) j.AddField("fr", from);
            if (!string.IsNullOrEmpty(runId)) j.AddField("run", runId);
            if (!string.IsNullOrEmpty(playlistId)) j.AddField("pl", playlistId);
            if (!string.IsNullOrEmpty(stop)) j.AddField("st", stop);
            if (!string.IsNullOrEmpty(player)) j.AddField("pp", player);
            if (!string.IsNullOrEmpty(roundOne)) { var rw = new JSONObject(JSONObject.Type.Array); rw.Add(roundOne); rw.Add(roundMany ?? roundOne + "s"); j.AddField("rw", rw); }
            j.AddField("at", at);
            return j.Print();
        }

        public static DreamieMoment FromJson(string json)
        {
            var j = SharedState.ParseFragment(json);
            if (j == null || j.type != JSONObject.Type.Object) return null;
            var m = new DreamieMoment
            {
                kind = PlaylistRunPlan.Str(j, "k", KindMoment),
                name = PlaylistRunPlan.Str(j, "n", null),
                guide = PlaylistRunPlan.Str(j, "g", null),
                priority = PlaylistRunPlan.Str(j, "p", Normal),
                once = PlaylistRunPlan.Bool(j, "o", false),
                cooldown = PlaylistRunPlan.Num(j, "cd"),
                text = PlaylistRunPlan.Str(j, "tx", null),
                emotion = PlaylistRunPlan.Str(j, "em", null),
                from = PlaylistRunPlan.Str(j, "fr", null),
                runId = PlaylistRunPlan.Str(j, "run", null),
                playlistId = PlaylistRunPlan.Str(j, "pl", null),
                stop = PlaylistRunPlan.Str(j, "st", null),
                player = PlaylistRunPlan.Str(j, "pp", null),
                at = PlaylistRunPlan.Num(j, "at"),
            };
            var d = j.GetField("d");
            m.dataJson = d != null && d.type == JSONObject.Type.Object ? d.Print() : "{}";
            var rwj = j.GetField("rw");
            if (rwj?.list != null && rwj.list.Count == 2) { m.roundOne = rwj.list[0].stringValue; m.roundMany = rwj.list[1].stringValue; }
            var pt = j.GetField("pt");
            if (pt != null && pt.type == JSONObject.Type.Array && pt.count == 3)
            {
                m.hasPoint = true;
                m.point = new Vector3((float)pt.list[0].doubleValue, (float)pt.list[1].doubleValue, (float)pt.list[2].doubleValue);
            }
            return m;
        }

        public override string ToString() => kind == KindSay ? $"say \"{text}\"" : $"moment {name} {dataJson}";
    }

    /// The director-side queue + the any-device entry points (CONTRACT § 4).
    public static class DreamieMoments
    {
        public const int MaxDataBytes = 1024;
        public const int MaxQueue = 32;
        public const int MaxRecent = 12;
        static readonly Regex NameRe = new Regex("^[a-z0-9_]{1,48}$");

        /// The platform's own moment names (same path as a title's).
        public static readonly string[] PlatformMoments =
        {
            "playlist_intro", "layout_appeared", "guide_room", "stop_intro", "progress_half", "progress_last", "stop_done",
            "stop_locked", "boss_intro", "stuck", "idle", "complete", "results",
        };

        // ── Hooks ─────────────────────────────────────────────────────
        public static Action<string> Log = m => Debug.Log(m);
        public static Func<double> Clock = () => DreamParkSession.Now;
        public static Func<string> DeviceId = () => PlayerPresence.MyId ?? "";
        /// This device runs Dreamie's director for the run (PlaylistRun sets it to "I am the run leader"). Solo = true.
        public static Func<bool> IsDirector = () => true;
        /// Non-director → director (PlaylistRun sets it: a SharedState request on the run). Null = accept locally.
        public static Action<DreamieMoment> Forward;
        /// Context the platform stamps on every moment (PlaylistRun sets it): (runId, playlistId, the round word).
        public static Func<(string runId, string playlistId, string roundOne, string roundMany)> Context;
        /// Record v2 (CONTRACT § 8): the run plan's part of Dreamie's §6 `context` for the stop in play (occurrence id or
        /// authored stop id, may be null) — JSON object text with `play`, `roundWord`, `dreamie {round, stop}`
        /// (PlaylistRunRules.DreamieContextJson). Set by PlaylistRunHost from the leading run; null / "{}" = nothing.
        public static Func<string, string> PlanContext;
        /// dreamie-live sets it: the director is speaking (or about to).
        public static Func<bool> BusyProbe;

        /// Raised on the DIRECTOR device for every accepted moment / say (after once / cooldown).
        public static event Action<DreamieMoment> Raised;
        /// Raised on every device when the title's own narrator starts / stops (Dreamie yields, never repeats it).
        public static event Action<bool> TitleSpeakingChanged;

        static readonly List<DreamieMoment> s_queue = new List<DreamieMoment>();
        static readonly List<DreamieMoment> s_recent = new List<DreamieMoment>();
        static readonly HashSet<string> s_once = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<string, double> s_last = new Dictionary<string, double>(StringComparer.Ordinal);
        static string s_onceRun;
        static bool s_titleSpeaking;

        public static int Count { get { lock (s_queue) return s_queue.Count; } }
        /// The last accepted moments (oldest first) — Dreamie's context ("recentSaid", what just happened).
        public static List<DreamieMoment> Recent() { lock (s_queue) return new List<DreamieMoment>(s_recent); }
        public static bool TitleSpeaking => s_titleSpeaking;
        public static bool Busy
        {
            get
            {
                if (s_titleSpeaking) return true;
                try { return BusyProbe != null && BusyProbe(); } catch (Exception) { return false; }
            }
        }

        public static bool ValidName(string name) => !string.IsNullOrEmpty(name) && NameRe.IsMatch(name);

        // ── Any device ────────────────────────────────────────────────

        /// dp.dreamie.moment(name, data, {guide, priority, point, once, cooldown}). False = refused (bad name, data > 1 KB).
        public static bool Moment(string name, string dataJson, string guide = null, string priority = null,
            GameObject pointObject = null, Vector3? point = null, bool once = false, double cooldown = 0, string stop = null, string player = null)
        {
            if (!ValidName(name)) { Log?.Invoke($"[DreamieMoments] refused moment '{name}': names are [a-z0-9_]"); return false; }
            string d = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson.Trim();
            if (Encoding.UTF8.GetByteCount(d) > MaxDataBytes) { Log?.Invoke($"[DreamieMoments] refused moment '{name}': data > {MaxDataBytes} bytes"); return false; }
            var dj = SharedState.ParseFragment(d);
            if (dj == null || dj.type != JSONObject.Type.Object) d = "{}";
            var m = new DreamieMoment
            {
                kind = DreamieMoment.KindMoment, name = name, dataJson = d, guide = string.IsNullOrEmpty(guide) ? null : guide,
                priority = priority == DreamieMoment.High || priority == DreamieMoment.Low ? priority : DreamieMoment.Normal,
                pointObject = pointObject, once = once, cooldown = Math.Max(0, cooldown), stop = stop, player = player,
            };
            if (point.HasValue) { m.hasPoint = true; m.point = point.Value; }
            else if (pointObject != null) { m.hasPoint = true; m.point = pointObject.transform.position; }
            return Submit(m);
        }

        /// dp.dreamie.say(text, {emotion}): exact words, voiced live.
        public static bool Say(string text, string emotion = null, string stop = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > 400) text = text.Substring(0, 400);
            return Submit(new DreamieMoment { kind = DreamieMoment.KindSay, name = "say", text = text.Trim(), emotion = emotion, priority = DreamieMoment.High, stop = stop });
        }

        /// dp.dreamie.title_speaking(bool): the title's own narrator talks — Dreamie yields and never repeats it.
        public static void SetTitleSpeaking(bool speaking)
        {
            ApplyTitleSpeaking(speaking);
            var m = new DreamieMoment { kind = "title", name = "title_speaking", dataJson = speaking ? "{\"on\":true}" : "{\"on\":false}" };
            Stamp(m);
            if (!Director() && Forward != null) { try { Forward(m); } catch (Exception e) { Log?.Invoke("[DreamieMoments] forward threw: " + e.Message); } }
        }

        internal static void ApplyTitleSpeaking(bool speaking)
        {
            if (s_titleSpeaking == speaking) return;
            s_titleSpeaking = speaking;
            try { TitleSpeakingChanged?.Invoke(speaking); } catch (Exception e) { Log?.Invoke("[DreamieMoments] listener threw: " + e.Message); }
        }

        static bool Director() { try { return IsDirector == null || IsDirector(); } catch (Exception) { return true; } }

        static void Stamp(DreamieMoment m)
        {
            if (string.IsNullOrEmpty(m.from)) { try { m.from = DeviceId?.Invoke() ?? ""; } catch (Exception) { m.from = ""; } }
            if (m.at <= 0) { try { m.at = Clock?.Invoke() ?? 0; } catch (Exception) { m.at = 0; } }
            if (Context != null && string.IsNullOrEmpty(m.runId))
                try { var c = Context(); m.runId = c.runId; m.playlistId = c.playlistId; m.roundOne ??= c.roundOne; m.roundMany ??= c.roundMany; } catch (Exception) { }
        }

        static bool Submit(DreamieMoment m)
        {
            Stamp(m);
            if (!Director() && Forward != null)
            {
                try { Forward(m); return true; }
                catch (Exception e) { Log?.Invoke("[DreamieMoments] forward threw (accepting locally): " + e.Message); }
            }
            return Accept(m);
        }

        // ── The director ──────────────────────────────────────────────

        /// Director side: a moment from this device or forwarded from another. False = filtered (once / cooldown / full).
        public static bool Accept(DreamieMoment m)
        {
            if (m == null) return false;
            if (m.kind == "title") { ApplyTitleSpeaking(m.dataJson != null && m.dataJson.Contains("true")); return true; }
            double now = m.at > 0 ? m.at : 0;
            try { now = Math.Max(now, Clock?.Invoke() ?? 0); } catch (Exception) { }
            lock (s_queue)
            {
                if (m.runId != s_onceRun) { s_onceRun = m.runId; s_once.Clear(); }
                if (m.kind == DreamieMoment.KindMoment)
                {
                    string ok = m.name + "|" + (m.stop ?? "");
                    if (m.once && s_once.Contains(ok)) return false;
                    if (s_last.TryGetValue(m.name, out double last) && m.cooldown > 0 && now - last < m.cooldown) return false;
                    if (m.once) s_once.Add(ok);
                    s_last[m.name] = now;
                }
                // Ordered by priority (high first), then arrival.
                int at = s_queue.Count;
                for (int i = 0; i < s_queue.Count; i++) if (s_queue[i].PriorityRank < m.PriorityRank) { at = i; break; }
                s_queue.Insert(at, m);
                while (s_queue.Count > MaxQueue)
                {
                    // Drop the oldest lowest-priority item.
                    int drop = s_queue.Count - 1;
                    s_queue.RemoveAt(drop);
                }
                s_recent.Add(m);
                while (s_recent.Count > MaxRecent) s_recent.RemoveAt(0);
            }
            Log?.Invoke($"[DreamieMoments] {m} ({m.priority}{(m.stop != null ? ", stop " + m.stop : "")}, from {m.from})");
            try { Raised?.Invoke(m); } catch (Exception e) { Log?.Invoke("[DreamieMoments] Raised listener threw: " + e.Message); }
            return true;
        }

        /// The next moment for the director (highest priority first). False = nothing queued.
        public static bool TryDequeue(out DreamieMoment m)
        {
            lock (s_queue)
            {
                if (s_queue.Count == 0) { m = null; return false; }
                m = s_queue[0];
                s_queue.RemoveAt(0);
                return true;
            }
        }

        public static bool TryPeek(out DreamieMoment m)
        {
            lock (s_queue) { m = s_queue.Count > 0 ? s_queue[0] : null; return m != null; }
        }

        /// Tests / a new run: forget the queue, once-set and cooldowns.
        public static void Reset()
        {
            lock (s_queue) { s_queue.Clear(); s_recent.Clear(); s_once.Clear(); s_last.Clear(); s_onceRun = null; }
            s_titleSpeaking = false;
        }

        internal static string Esc(string s) => SharedState.JsonEscape(s ?? "");
        internal static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
