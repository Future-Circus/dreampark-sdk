// ─────────────────────────────────────────────────────────────────────
//  PlaylistRunCache.cs — offline cache + resume of the last playlist run, and the
//  outbox (repair-oct20 CONTRACT § 3 "Offline + resume").
//
//  The device keeps its last run: the rev-pinned plan (every round it saw), the goal
//  blocks, the Dreamie script, and progress (round, finished stops + results, flags,
//  scores). A headset that wakes / starts a session with no Wi-Fi resumes it per the
//  EXISTING login rule (ProfilePairingHandler.ShouldAutoReleaseLogin = managed guest mode):
//    consumer / keep-login → resume exactly (same rev, round, results, scores)
//    managed guest mode   → same playlist, progress reset (round 1, empty board)
//    no account           → nothing (the headset stays blank)
//  Offline end = Results + Play Again. Play events, badge awards, ratings and the
//  queue's `complete` go to the outbox, flushed when the server is reachable.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    public static class PlaylistResumeRules
    {
        public enum Mode { None, Exact, ResetProgress }

        /// The one resume rule (no second rule): account-less → none; managed guest mode (auto-release) → same playlist,
        /// progress reset; otherwise exactly where it was. A cache from another account never resumes exactly.
        public static Mode Decide(bool hasAccount, bool autoReleaseLogin, bool hasCache, string cacheAccount, string account)
        {
            if (!hasAccount || !hasCache) return Mode.None;
            if (autoReleaseLogin) return Mode.ResetProgress;
            if (!string.IsNullOrEmpty(cacheAccount) && !string.IsNullOrEmpty(account) && cacheAccount != account) return Mode.ResetProgress;
            return Mode.Exact;
        }

        /// Where a resumed run starts: the cached round (exact) or round 1 (reset); a finished run starts over.
        public static int StartLeg(Mode mode, int cachedLeg, bool cachedComplete, int legCount)
        {
            if (mode != Mode.Exact || cachedComplete) return 0;
            return Math.Max(0, Math.Min(cachedLeg, Math.Max(0, legCount - 1)));
        }
    }

    public static class PlaylistRunCache
    {
        public static Func<string> Dir = () => Path.Combine(Application.persistentDataPath, "dreampark", "playlist-run");
        public static Func<string> Account = () => null;
        static string s_last;

        public static string FilePath => Path.Combine(Dir(), "last.json");

        /// This device's copy of the run (everything needed to play it to the end offline).
        public static string Snapshot(PlaylistRun run, IPlaylistLeg leg, string deviceId)
        {
            var j = new JSONObject();
            var plan = leg.Plan;
            j.AddField("v", 1);
            j.AddField("runId", run.RunId ?? "");
            j.AddField("playlistId", plan.playlistId ?? "");
            j.AddField("rev", plan.rev ?? "");
            j.AddField("contentId", plan.contentId ?? "");
            j.AddField("name", plan.name ?? "");
            j.AddField("device", deviceId ?? "");
            string acct = null;
            try { acct = Account?.Invoke(); } catch (Exception) { }
            if (!string.IsNullOrEmpty(acct)) j.AddField("account", acct);
            j.AddField("savedAt", DateTime.UtcNow.ToString("o"));
            j.AddField("leg", plan.legIndex);
            j.AddField("legCount", plan.LegCount);
            j.AddField("phase", leg.Row.phase ?? "");
            j.AddField("plan", SharedState.ParseFragment(plan.ToJson()) ?? new JSONObject());
            // The goal blocks of every title the plan uses.
            var goals = new JSONObject();
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plan.contentId ?? "" };
            foreach (var l in plan.legs) foreach (var s in l?.stops ?? new List<PlaylistRunStop>()) if (s != null) titles.Add(plan.ContentOf(s) ?? "");
            foreach (var t in titles)
            {
                if (string.IsNullOrEmpty(t)) continue;
                var tg = new JSONObject();
                foreach (var kv in PlaylistGoalLibrary.All(t)) tg.AddField(kv.Key, kv.Value);
                if (tg.count > 0) goals.AddField(PlaylistGoalLibrary.BaseTitle(t), tg);
            }
            j.AddField("goals", goals);
            var prog = new JSONObject();
            foreach (var kv in leg.Progress) if (kv.Value != null) prog.AddField(kv.Key, SharedState.ParseFragment(kv.Value.ToJson()) ?? new JSONObject());
            j.AddField("progress", prog);
            var res = new JSONObject();
            foreach (var k in run.Keys)
                if (k.StartsWith("res.", StringComparison.Ordinal)) { var r = SharedState.ParseFragment(run.ResultJson(k.Substring(4))); if (r != null) res.AddField(k.Substring(4), r); }
            j.AddField("results", res);
            var rs = SharedState.ParseFragment(run.RunStateJson);
            if (rs != null) j.AddField("runState", rs);
            string board = PlaylistScoreboard.BoardJson(PlaylistRunRules.DefaultScoring(plan));
            var bj = board != null ? SharedState.ParseFragment(board) : null;
            if (bj != null) j.AddField("scoreboard", bj);
            return j.Print();
        }

        public static void Write(string json)
        {
            if (string.IsNullOrEmpty(json) || json == s_last) return;
            // The savedAt stamp changes every write; compare the rest so an idle run does not rewrite the file.
            s_last = json;
            try
            {
                Directory.CreateDirectory(Dir());
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e) { Debug.LogWarning("[pl-runtime] cache write failed: " + e.Message); }
        }

        public static JSONObject Read()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var j = new JSONObject(File.ReadAllText(FilePath));
                return j.type == JSONObject.Type.Object ? j : null;
            }
            catch (Exception) { return null; }
        }

        /// The cached run's plan for a resume (goal blocks re-registered offline). Null = none / not resumable.
        public static PlaylistRunPlan Plan(JSONObject cache, PlaylistResumeRules.Mode mode, out int startLeg)
        {
            startLeg = 0;
            if (cache == null || mode == PlaylistResumeRules.Mode.None) return null;
            var p = PlaylistRunPlan.FromJson(cache.GetField("plan")?.Print() ?? "");
            if (p == null) return null;
            var goals = cache.GetField("goals");
            if (goals?.keys != null)
                for (int i = 0; i < goals.keys.Count; i++) PlaylistGoalLibrary.RegisterBackend(goals.keys[i], goals.list[i].Print());
            startLeg = PlaylistResumeRules.StartLeg(mode, (int)PlaylistRunPlan.Num(cache, "leg"),
                PlaylistRunPlan.Str(cache, "phase", "") == PlaylistRunRules.Complete, (int)PlaylistRunPlan.Num(cache, "legCount", p.LegCount));
            p.legIndex = startLeg;
            // A sets playlist (pl-sets): the cached set is known again (the resume packs it, followers resolve it).
            if (p.sets) foreach (var l in p.legs) { var set = PlaylistSets.FromLeg(l); if (set != null) PlaylistSets.Register(p.playlistId, set); }
            return p;
        }
    }

    /// The offline outbox: play events, badge awards, ratings, the queue's `complete`. File-backed, flushed when reachable,
    /// oldest first (a `queue_complete` for round 2 never overtakes round 1's).
    public static class PlaylistOutbox
    {
        public enum Answer { Done, Retry, Drop }

        public static Func<string> Dir = () => Path.Combine(Application.persistentDataPath, "dreampark", "playlist-run");
        /// (kind, body JSON) → true when delivered (synchronous senders: tests). Null = keep everything queued.
        public static Func<string, string, bool> Send;
        /// (kind, body JSON, answer) — the asynchronous sender core sets (device-key routes). Wins over Send.
        public static Action<string, string, Action<Answer>> SendAsync;
        /// Where this device plays (parkId, locationId), stamped on a queue_complete when it is queued.
        public static Func<(string parkId, string locationId)> Place;
        /// Seconds before a Retry is tried again.
        public static double RetrySeconds = 15;
        public static Func<double> Clock = () => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        public static Action<string> Log = m => Debug.Log(m);
        static bool s_inFlight;
        static double s_nextAt;

        /// The headset queue route's answer → what the outbox does (Web `POST /app/queue/:park/:loc/complete`):
        /// DROP on changed / ALREADY_COMPLETE / LEG_NOT_FOUND / STALE_COMPLETE (and a 400 LEG_MISMATCH: the request is wrong,
        /// logged); RETRY on NOT_CURRENT / NOT_PLAYING, access not granted yet (403) and anything offline / 5xx.
        public static Answer QueueCompleteAnswer(bool ok, long status, string text)
        {
            JSONObject j = null;
            try { if (!string.IsNullOrEmpty(text)) j = new JSONObject(text); } catch (Exception) { }
            string reason = PlaylistRunPlan.Str(j, "reason", null) ?? PlaylistRunPlan.Str(j, "code", null) ?? PlaylistRunPlan.Str(j, "error", null) ?? "";
            if (ok && j != null && PlaylistRunPlan.Bool(j, "changed", false)) return Answer.Done;
            switch (reason)
            {
                case "ALREADY_COMPLETE": case "LEG_NOT_FOUND": case "STALE_COMPLETE": return Answer.Drop;
                case "NOT_CURRENT": case "NOT_PLAYING": return Answer.Retry;
                case "LEG_MISMATCH": return Answer.Drop;
            }
            if (status == 400) return Answer.Drop;
            if (ok && j != null) return Answer.Drop;   // success without a change and no known reason: nothing left to do
            return Answer.Retry;                       // offline, 401/403 (not at the location yet), 5xx
        }
        public const int Max = 500;
        static readonly object s_lock = new object();
        static List<string> s_items;

        static string FilePath => Path.Combine(Dir(), "outbox.jsonl");

        static void Load()
        {
            if (s_items != null) return;
            s_items = new List<string>();
            try { if (File.Exists(FilePath)) foreach (var l in File.ReadAllLines(FilePath)) if (!string.IsNullOrWhiteSpace(l)) s_items.Add(l); }
            catch (Exception) { }
        }

        static void Save()
        {
            try { Directory.CreateDirectory(Dir()); File.WriteAllLines(FilePath, s_items); } catch (Exception) { }
        }

        public static int Count { get { lock (s_lock) { Load(); return s_items.Count; } } }

        public static void Enqueue(string kind, string bodyJson)
        {
            lock (s_lock)
            {
                Load();
                var j = new JSONObject();
                j.AddField("k", kind ?? "");
                var b = SharedState.ParseFragment(bodyJson) ?? new JSONObject();
                if (kind == "queue_complete" && Place != null)
                {
                    try
                    {
                        var (park, loc) = Place();
                        if (!string.IsNullOrEmpty(park) && !b.HasField("parkId")) b.AddField("parkId", park);
                        if (!string.IsNullOrEmpty(loc) && !b.HasField("locationId")) b.AddField("locationId", loc);
                    }
                    catch (Exception) { }
                }
                if (!b.HasField("at")) b.AddField("at", DateTime.UtcNow.ToString("o"));
                j.AddField("b", b);
                j.AddField("at", DateTime.UtcNow.ToString("o"));
                s_items.Add(j.Print());
                while (s_items.Count > Max) s_items.RemoveAt(0);
                Save();
            }
        }

        /// Asynchronous delivery (SendAsync): one item in flight, oldest first; Done/Drop removes it and goes on, Retry
        /// waits RetrySeconds. Safe to call every frame.
        public static void FlushAsync()
        {
            if (SendAsync == null || s_inFlight) return;
            double now = Clock();
            if (now < s_nextAt) return;
            string item;
            lock (s_lock) { Load(); if (s_items.Count == 0) return; item = s_items[0]; }
            var j = SharedState.ParseFragment(item);
            string kind = PlaylistRunPlan.Str(j, "k", "");
            s_inFlight = true;
            try
            {
                SendAsync(kind, j?.GetField("b")?.Print() ?? "{}", answer =>
                {
                    s_inFlight = false;
                    if (answer == Answer.Retry) { s_nextAt = Clock() + RetrySeconds; return; }
                    lock (s_lock) { Load(); if (s_items.Count > 0 && s_items[0] == item) { s_items.RemoveAt(0); Save(); } }
                    Log?.Invoke($"[pl-runtime] outbox {kind}: {(answer == Answer.Done ? "delivered" : "dropped")} ({Count} left)");
                    s_nextAt = 0;
                });
            }
            catch (Exception e) { s_inFlight = false; s_nextAt = Clock() + RetrySeconds; Log?.Invoke("[pl-runtime] outbox send threw: " + e.Message); }
        }

        /// Deliver what can be delivered (oldest first); stops at the first failure. Returns how many went out.
        public static int Flush()
        {
            if (SendAsync != null) { FlushAsync(); return 0; }
            if (Send == null) return 0;
            int sent = 0;
            lock (s_lock)
            {
                Load();
                while (s_items.Count > 0)
                {
                    var j = SharedState.ParseFragment(s_items[0]);
                    bool ok = false;
                    try { ok = j != null && Send(PlaylistRunPlan.Str(j, "k", ""), j.GetField("b")?.Print() ?? "{}"); } catch (Exception) { ok = false; }
                    if (!ok) break;
                    s_items.RemoveAt(0);
                    sent++;
                }
                if (sent > 0) Save();
            }
            return sent;
        }

        /// Tests.
        public static void Reset() { lock (s_lock) { s_items = new List<string>(); } s_inFlight = false; s_nextAt = 0; }
        public static List<string> Items() { lock (s_lock) { Load(); return new List<string>(s_items); } }
    }
}
