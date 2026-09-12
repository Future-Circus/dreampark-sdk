// ─────────────────────────────────────────────────────────────────────────
//  ScoreAPI.cs
//
//  DreamPark SDK — game scores & leaderboards
//
//  Submit a score to a named "board" (contentId + metric, optionally scoped
//  to one attraction) and read it back three ways: the calling player's own
//  best, a global top-N across every park, or a top-N at the park the player
//  is currently in — any of those crossed with "friends only." Backed by
//  /app/profile/scores/* on the backend (same identity/auth model as
//  ProfileAPI/GameStorageAPI — headset binding in production, preview key
//  in SDK editor testing).
//
//  This is NOT a copy of GameStorageAPI's high-score primitive (Max/Min).
//  GameStorageAPI stores YOUR OWN data — one Firestore doc per (identity,
//  game), read synchronously from a local cache that's yours to keep stale
//  or fresh. A leaderboard is fundamentally SOMEONE ELSE'S data changing
//  without your knowledge: there is no correct client-side cache for "am I
//  still #3," because the answer can change from a submit you never made.
//  So every read here is an async round trip, every time — there is no
//  IsReady/OnReady local-cache path, on purpose.
//
//
//  ── WRITES REQUIRE THE PLAYER TO ENTER YOUR ATTRACTION FIRST ──
//  Same ContentGate consent latch as ProfileAPI/GameStorageAPI — see either
//  file's header for the full reasoning. Reads are never gated.
//
//
//  ── WHY THIS REUSES SO MUCH OF PROFILEAPI / GAMESTORAGEAPI ──
//  A leaderboard write needs the same three things a badge award needs:
//  identity, consent, and "where was this earned." Reimplementing any of
//  them here would be a second place those policies could drift:
//    • ContentGate consent  → ProfileAPI.GatedPost (now internal)
//    • park/shard stamping  → ProfileAPI.CallingParkId() + ShardContext,
//      the exact fields/method AwardBadge already stamps awards with
//    • attraction slugging  → GameStorageAPI.Slugify (same algorithm the
//      server's boardId composition re-derives independently)
//    • metric key charset   → GameStorageAPI.KeyRe (a metric is embedded in
//      a server-side boardId the same way a storage key names a doc field)
//    • per-script scope     → GameStorageAPI.ResolveScopeTable (now
//      internal) walks GameArea → PropTemplate → LevelTemplate; duplicating
//      that walk risks the two resolvers disagreeing about which template
//      wins for a given prefab)
//
//
//  ── THE UNBOUND GAP ──
//  A round that ends in the first seconds of a session — before the QR/
//  Swift pairing round-trip lands — would otherwise lose its score outright.
//  ProfileAPI.AwardBadge closes an analogous gap with a bounded held-award
//  queue keyed on "a claim for a KNOWN identity is in flight" (BindPending).
//  That narrower signal doesn't exist here: per the backend, EVERY write
//  resolves to a concrete identity doc — real account or guest DreamID —
//  the instant one exists, so there is no separate "claim pending" state to
//  hold against. The only real gap is "nothing has bound at all yet," which
//  is exactly the condition GameStorageAPI already handles by queuing writes
//  locally and replaying them on OnIdentityBound. SubmitScore follows that
//  simpler shape: a small bounded pending list, flushed whole on the next
//  identity bind (real or guest), dropped on OnIdentityCleared. Values are
//  captured AT THE CALL, not re-read at flush time — same reasoning as
//  AwardBadge's held contentId: replaying with a freshly-read park/attraction
//  would attribute the run to wherever the player wandered while pairing
//  was still resolving.
//
//
//  ── ONE WIRE FIELD IS PROVISIONAL ──
//  `direction` ("desc"/"asc" — which end of the board wins, e.g. race lap
//  times want ascending) is Web's proposed field name as of this writing,
//  raised with Captain/Aidan separately because it's a submit-contract
//  change, not settled. If it ships under a different name, every call site
//  below funnels through DirectionWire() — one line to change.
//
//
//  Calling pattern (C#):
//      ScoreAPI.SubmitScore("WizardsWay", "high_score", 9410);
//      ScoreAPI.SubmitScore("CrashCourse", "lap_time", 42.7, attraction: "canyon-race",
//          direction: ScoreDirection.Ascending);
//      ScoreAPI.FetchLeaderboard("WizardsWay", "high_score", thisParkOnly: true,
//          friendsOnly: false, done: (ok, board) => { ... });
//
//  Calling pattern (Lua) — a GLOBAL `dp.score` namespace, not a per-script
//  auto-resolving proxy like `storage`: a score is closer in spirit to the
//  player-global concepts in `dp.profile` (a badge, an achievement) than to
//  storage's per-script convenience, and it avoids adding a second injected
//  local to LuaBehaviour for this first version. A script that wants the
//  auto-resolved (gameId, attraction) `storage` already has can just read
//  `storage.gameId` / `storage.attraction` at the call site:
//      dp.score.submit(storage.gameId, "high_score", finalScore, storage.attraction)
//      dp.score.fetchLeaderboard(storage.gameId, "high_score",
//          { thisParkOnly = true }, function(ok, board) ... end)
// ─────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Text;
using Defective.JSON;
using UnityEngine;
using XLua;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{
    public enum ScoreDirection { Descending = 0, Ascending = 1 }

    public enum LeaderboardWindow { All = 0, Day = 1, Week = 2, Month = 3 }

    /// <summary>One board's authoritative best, echoed back by SubmitScore.
    /// `hasPreviousBest` is false on a board's first-ever entry — never
    /// confuse that with a previous best of 0, which is a real score.</summary>
    [System.Serializable]
    public class ScoreBoardResult
    {
        public double best;
        public string atIso;
        public bool hasPreviousBest;
        public double previousBest;
        public bool improved;
    }

    [System.Serializable]
    public class ScoreSubmitResult
    {
        public string contentId;
        public string attraction;   // null for the game-level board
        public string metric;
        public double submittedValue;
        public string submittedAtIso;
        public bool accepted;       // true if this write changed at least one board
        public ScoreBoardResult global;
        public ScoreBoardResult park;   // null when the submit carried no parkId
        public string userId;
        public string dreamId;
    }

    [System.Serializable]
    public class LeaderboardEntry
    {
        public int rank;
        public string userId;
        public string dreamId;
        public string displayName;
        public string avatarUrl;
        public double score;
        public string updatedAtIso;
        public bool isMe;
    }

    /// <summary>The calling player's own row inside a fetched leaderboard
    /// PAGE (only present when includeMe was requested). Rank here is
    /// relative to whatever filter produced the page — a friends-filtered
    /// fetch's rank is standing among friends, not a global rank.</summary>
    [System.Serializable]
    public class LeaderboardMyRow
    {
        public int rank;
        public double score;
        public string updatedAtIso;
    }

    [System.Serializable]
    public class LeaderboardResult
    {
        public List<LeaderboardEntry> entries = new List<LeaderboardEntry>();
        public string nextCursor;   // null when there is no further page
        public LeaderboardMyRow me; // null unless includeMe was requested and resolvable
        public int total;
    }

    /// <summary>A dedicated cheap read for "what's MY score" — no board scan,
    /// unlike FetchLeaderboard. `hasScore`/`hasRank` are false rather than
    /// the value being 0, because 0 is a real score and "never attempted" is
    /// a different, distinct answer the mobile app needs to tell apart.</summary>
    [System.Serializable]
    public class MyScoreResult
    {
        public bool hasScore;
        public double score;
        public bool hasRank;
        public int rank;
        public int total;
        public string updatedAtIso;
    }

    [LuaCallCSharp]
    public static class ScoreAPI
    {
        // ── Pending submissions banked while no identity has bound yet ────
        // See the header comment above for why this is a small bounded
        // list flushed on OnIdentityBound, not GameStorageAPI's full
        // op-queue/coalescing machinery (a score submit is a rare event,
        // never a per-frame one, so there is nothing to coalesce) and not
        // ProfileAPI's narrower BindPending claim-in-flight window (which
        // this system doesn't have, per the backend's own identity model).
        const int   MaxPendingSubmits    = 16;
        const float PendingSubmitTtlSec  = 90f;

        struct PendingSubmit
        {
            public string contentId, metric, attraction, parkId, shardId;
            public double value;
            public ScoreDirection direction;
            public Action<bool, ScoreSubmitResult> done;
            public float atSec;
        }

        static readonly List<PendingSubmit> _pending = new List<PendingSubmit>();

        static bool _identityHooked;
        static void HookIdentity()
        {
            if (_identityHooked) return;
            _identityHooked = true;
            ProfileAPI.OnIdentityBound += FlushPending;
            // The player left. A held submit must never survive into
            // whoever picks the headset up next — same rule as ProfileAPI's
            // held badge awards.
            ProfileAPI.OnIdentityCleared += () => DropPending("identity cleared");
        }

        static void FlushPending()
        {
            if (_pending.Count == 0) return;
            var held = _pending.ToArray();
            _pending.Clear();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < held.Length; i++)
            {
                var p = held[i];
                if (now - p.atSec > PendingSubmitTtlSec)
                {
                    Debug.LogWarning($"[ScoreAPI] Dropped held score submit '{p.metric}' — identity took longer than {PendingSubmitTtlSec:0}s to bind.");
                    try { p.done?.Invoke(false, null); } catch (Exception e) { Debug.LogWarning(e); }
                    continue;
                }
                SubmitScoreNow(p.contentId, p.metric, p.value, p.attraction, p.direction, p.parkId, p.shardId, p.done);
            }
        }

        static void DropPending(string why)
        {
            if (_pending.Count == 0) return;
            Debug.LogWarning($"[ScoreAPI] Dropping {_pending.Count} held score submit(s) — {why}.");
            var held = _pending.ToArray();
            _pending.Clear();
            for (int i = 0; i < held.Length; i++)
            {
                try { held[i].done?.Invoke(false, null); } catch (Exception e) { Debug.LogWarning(e); }
            }
        }

        // ── Writes ─────────────────────────────────────────────────────────

        /// <summary>Submit a score. The server decides whether it beats the
        /// existing best for this board (both globally and at the calling
        /// park, if known) — the client never computes or trusts its own
        /// "best," it just reports a value and reads back what happened.
        ///
        /// `direction` only matters on a board's FIRST-EVER write: the sort
        /// order locks there. A later call disagreeing is rejected by the
        /// server (not silently reinterpreted), so pick it correctly the
        /// first time — Descending (higher wins) for most scores, Ascending
        /// for anything timed (lap times, speedruns).</summary>
        public static void SubmitScore(
            string contentId,
            string metric,
            double value,
            string attraction = null,
            ScoreDirection direction = ScoreDirection.Descending,
            Action<bool, ScoreSubmitResult> done = null)
        {
            if (string.IsNullOrEmpty(contentId)) { Debug.LogWarning("[ScoreAPI] SubmitScore requires a contentId."); done?.Invoke(false, null); return; }
            if (string.IsNullOrEmpty(metric) || !GameStorageAPI.KeyRe.IsMatch(metric))
            {
                Debug.LogWarning($"[ScoreAPI] SubmitScore rejected — invalid metric '{metric}', must match [A-Za-z0-9_-]{{1,64}}.");
                done?.Invoke(false, null);
                return;
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                Debug.LogWarning("[ScoreAPI] SubmitScore rejected — value must be finite.");
                done?.Invoke(false, null);
                return;
            }

            HookIdentity();

            if (!ProfileAPI.IsBound)
            {
                if (_pending.Count >= MaxPendingSubmits)
                {
                    Debug.LogWarning("[ScoreAPI] SubmitScore dropped — no identity bound yet and the pending queue is full.");
                    done?.Invoke(false, null);
                    return;
                }
                // Captured NOW — see the header comment on why re-reading
                // park/attraction at flush time would be wrong.
                _pending.Add(new PendingSubmit
                {
                    contentId = contentId,
                    metric = metric,
                    value = value,
                    attraction = attraction,
                    direction = direction,
                    parkId = ProfileAPI.CallingParkId(),
                    shardId = ProfileAPI.ShardContext,
                    done = done,
                    atSec = Time.realtimeSinceStartup,
                });
                Debug.Log($"[ScoreAPI] SubmitScore('{metric}') held — no identity bound yet.");
                return;
            }

            SubmitScoreNow(contentId, metric, value, attraction, direction, ProfileAPI.CallingParkId(), ProfileAPI.ShardContext, done);
        }

        static void SubmitScoreNow(
            string contentId, string metric, double value, string attraction,
            ScoreDirection direction, string parkId, string shardId,
            Action<bool, ScoreSubmitResult> done)
        {
            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("metric", metric);
            var slug = GameStorageAPI.Slugify(attraction);
            if (!string.IsNullOrEmpty(slug)) body.AddField("attraction", slug);
            body.AddField("value", value);
            // See the header note — wire field name pending final confirmation.
            body.AddField("direction", DirectionWire(direction));
            if (!string.IsNullOrEmpty(parkId)) body.AddField("parkId", parkId);
            if (!string.IsNullOrEmpty(shardId)) body.AddField("shardId", shardId);

            var url = "/app/profile/scores/" + Uri.EscapeDataString(contentId);
            ProfileAPI.GatedPost(url, ProfileAPI.AuthHeader(), body, (ok, resp) =>
            {
                if (!ok || resp?.json == null) { done?.Invoke(false, null); return; }
                done?.Invoke(true, HydrateSubmitResult(resp.json));
            });
        }

        // ── Reads (always a round trip — never cached, see header) ───────

        /// <summary>Cheap read for "what's my score" — no board scan, unlike
        /// FetchLeaderboard. Requires an identity to be bound (a guest who
        /// hasn't paired at all has nothing to ask about); does NOT require
        /// a claimed account — an anonymous DreamID has its own row.</summary>
        public static void FetchMyScore(
            string contentId,
            string metric,
            string attraction = null,
            bool thisParkOnly = false,
            LeaderboardWindow window = LeaderboardWindow.All,
            Action<bool, MyScoreResult> done = null)
        {
            if (!ValidateReadArgs(contentId, metric, thisParkOnly, "FetchMyScore")) { done?.Invoke(false, null); return; }
            if (!ProfileAPI.IsBound) { Debug.LogWarning("[ScoreAPI] FetchMyScore with no identity bound."); done?.Invoke(false, null); return; }

            var qs = BuildQuery(metric, attraction, thisParkOnly, friendsOnly: false, window, limit: null, cursor: null, includeMe: false);
            var url = $"/app/profile/scores/{Uri.EscapeDataString(contentId)}/me{qs}";
            DreamParkAPI.GET(url, ProfileAPI.AuthHeader(), (ok, resp) =>
            {
                if (!ok || resp?.json == null) { done?.Invoke(false, null); return; }
                done?.Invoke(true, HydrateMyScore(resp.json));
            });
        }

        /// <summary>Fetch a page of a leaderboard. `thisParkOnly` and
        /// `friendsOnly` are independent axes — both can be true at once
        /// ("my friends, at this park") — NOT one combined scope enum, since
        /// global+friends and park+friends are both real, useful boards.
        ///
        /// `friendsOnly` requires a claimed account: friendship is uid-to-uid,
        /// and a guest DreamID binding has no uid to match against, so this
        /// fails fast client-side with a warning rather than round-tripping
        /// to the server's 400.</summary>
        public static void FetchLeaderboard(
            string contentId,
            string metric,
            string attraction = null,
            bool thisParkOnly = false,
            bool friendsOnly = false,
            int limit = 25,
            string cursor = null,
            bool includeMe = true,
            LeaderboardWindow window = LeaderboardWindow.All,
            Action<bool, LeaderboardResult> done = null)
        {
            if (!ValidateReadArgs(contentId, metric, thisParkOnly, "FetchLeaderboard")) { done?.Invoke(false, null); return; }
            if (!ProfileAPI.IsBound) { Debug.LogWarning("[ScoreAPI] FetchLeaderboard with no identity bound."); done?.Invoke(false, null); return; }
            if (friendsOnly && string.IsNullOrEmpty(ProfileAPI.BoundUserId))
            {
                Debug.LogWarning("[ScoreAPI] FetchLeaderboard friendsOnly requires a claimed account — a guest DreamID has no friend graph.");
                done?.Invoke(false, null);
                return;
            }

            limit = Mathf.Clamp(limit, 1, 100);
            // A cursor from a friends=true page and a cursor from an
            // unfiltered page are NOT interchangeable server-side (the
            // friends fetch merges multiple chunked queries in memory) — the
            // server rejects a mismatched one rather than silently paging
            // wrong, so we don't try to detect that mismatch here.
            var qs = BuildQuery(metric, attraction, thisParkOnly, friendsOnly, window, limit, cursor, includeMe);
            var url = $"/app/profile/scores/{Uri.EscapeDataString(contentId)}/leaderboard{qs}";
            DreamParkAPI.GET(url, ProfileAPI.AuthHeader(), (ok, resp) =>
            {
                if (!ok || resp?.json == null) { done?.Invoke(false, null); return; }
                done?.Invoke(true, HydrateLeaderboard(resp.json));
            });
        }

        static bool ValidateReadArgs(string contentId, string metric, bool thisParkOnly, string caller)
        {
            if (string.IsNullOrEmpty(contentId)) { Debug.LogWarning($"[ScoreAPI] {caller} requires a contentId."); return false; }
            if (string.IsNullOrEmpty(metric) || !GameStorageAPI.KeyRe.IsMatch(metric))
            {
                Debug.LogWarning($"[ScoreAPI] {caller} rejected — invalid metric '{metric}', must match [A-Za-z0-9_-]{{1,64}}.");
                return false;
            }
            if (thisParkOnly && string.IsNullOrEmpty(ProfileAPI.CallingParkId()))
            {
                Debug.LogWarning($"[ScoreAPI] {caller} thisParkOnly requires a known park (ProfileAPI.ParkContext / SessionContext.LocationId) — neither is set.");
                return false;
            }
            return true;
        }

        static string BuildQuery(string metric, string attraction, bool thisParkOnly, bool friendsOnly,
            LeaderboardWindow window, int? limit, string cursor, bool includeMe)
        {
            var sb = new StringBuilder();
            void Add(string k, string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                sb.Append(sb.Length == 0 ? "?" : "&").Append(k).Append('=').Append(Uri.EscapeDataString(v));
            }

            Add("metric", metric);
            var slug = GameStorageAPI.Slugify(attraction);
            if (!string.IsNullOrEmpty(slug)) Add("attraction", slug);

            if (thisParkOnly)
            {
                Add("scope", "park");
                Add("parkId", ProfileAPI.CallingParkId());
                if (!string.IsNullOrEmpty(ProfileAPI.ShardContext)) Add("shardId", ProfileAPI.ShardContext);
            }
            else
            {
                Add("scope", "global");
            }

            if (friendsOnly) Add("friends", "true");
            if (window != LeaderboardWindow.All) Add("window", WindowWire(window));
            if (limit.HasValue) Add("limit", limit.Value.ToString());
            if (!string.IsNullOrEmpty(cursor)) Add("cursor", cursor);
            if (includeMe) Add("includeMe", "true");
            return sb.ToString();
        }

        static string DirectionWire(ScoreDirection d) => d == ScoreDirection.Ascending ? "asc" : "desc";

        static string WindowWire(LeaderboardWindow w)
        {
            switch (w)
            {
                case LeaderboardWindow.Day:   return "day";
                case LeaderboardWindow.Week:  return "week";
                case LeaderboardWindow.Month: return "month";
                default:                      return "all";
            }
        }

        static LeaderboardWindow WindowFromWire(string w)
        {
            switch (w)
            {
                case "day":   return LeaderboardWindow.Day;
                case "week":  return LeaderboardWindow.Week;
                case "month": return LeaderboardWindow.Month;
                default:      return LeaderboardWindow.All;
            }
        }

        // ── JSON hydration ───────────────────────────────────────────────

        static ScoreBoardResult HydrateBoard(JSONObject b)
        {
            if (b == null || b.type == JSONObject.Type.Null) return null;
            var prev = b.GetField("previousBest");
            bool hasPrev = prev != null && prev.type != JSONObject.Type.Null;
            return new ScoreBoardResult
            {
                best            = b.GetField("best")?.floatValue ?? 0,
                atIso           = b.GetField("at")?.stringValue,
                hasPreviousBest = hasPrev,
                previousBest    = hasPrev ? prev.floatValue : 0,
                improved        = b.GetField("improved")?.boolValue ?? false,
            };
        }

        static ScoreSubmitResult HydrateSubmitResult(JSONObject json)
        {
            if (json == null) return null;
            var r = new ScoreSubmitResult
            {
                contentId  = json.GetField("contentId")?.stringValue,
                attraction = json.GetField("attraction")?.stringValue,
                metric     = json.GetField("metric")?.stringValue,
                accepted   = json.GetField("accepted")?.boolValue ?? false,
                global     = HydrateBoard(json.GetField("global")),
                park       = HydrateBoard(json.GetField("byPark")),
            };
            var submitted = json.GetField("submitted");
            if (submitted != null)
            {
                r.submittedValue = submitted.GetField("value")?.floatValue ?? 0;
                r.submittedAtIso = submitted.GetField("at")?.stringValue;
            }
            var identity = json.GetField("identity");
            if (identity != null)
            {
                r.userId  = identity.GetField("userId")?.stringValue;
                r.dreamId = identity.GetField("dreamId")?.stringValue;
            }
            return r;
        }

        static bool IsCallingIdentity(string userId, string dreamId)
        {
            if (!string.IsNullOrEmpty(ProfileAPI.BoundUserId) && userId == ProfileAPI.BoundUserId) return true;
            if (!string.IsNullOrEmpty(ProfileAPI.BoundDreamId) && dreamId == ProfileAPI.BoundDreamId) return true;
            return false;
        }

        static LeaderboardResult HydrateLeaderboard(JSONObject json)
        {
            var result = new LeaderboardResult();
            if (json == null) return result;

            result.total      = (int)(json.GetField("total")?.floatValue ?? 0);
            result.nextCursor = json.GetField("nextCursor")?.stringValue;

            var entries = json.GetField("entries");
            if (entries != null && entries.type == JSONObject.Type.Array && entries.list != null)
            {
                for (int i = 0; i < entries.list.Count; i++)
                {
                    var e = entries.list[i];
                    var entry = new LeaderboardEntry
                    {
                        rank        = (int)(e.GetField("rank")?.floatValue ?? 0),
                        userId      = e.GetField("userId")?.stringValue,
                        dreamId     = e.GetField("dreamId")?.stringValue,
                        displayName = e.GetField("displayName")?.stringValue,
                        avatarUrl   = e.GetField("avatarUrl")?.stringValue,
                        score       = e.GetField("score")?.floatValue ?? 0,
                        updatedAtIso = e.GetField("updatedAt")?.stringValue,
                    };
                    entry.isMe = IsCallingIdentity(entry.userId, entry.dreamId);
                    result.entries.Add(entry);
                }
            }

            var me = json.GetField("me");
            if (me != null && me.type != JSONObject.Type.Null)
            {
                var myRank = (int)(me.GetField("rank")?.floatValue ?? 0);
                result.me = new LeaderboardMyRow
                {
                    rank         = myRank,
                    score        = me.GetField("score")?.floatValue ?? 0,
                    updatedAtIso = me.GetField("updatedAt")?.stringValue,
                };
                // The appended "me" row may duplicate one already in this
                // page (includeMe appends even when already present) —
                // mark it rather than leave two conflicting truths.
                for (int i = 0; i < result.entries.Count; i++)
                    if (result.entries[i].rank == myRank) result.entries[i].isMe = true;
            }

            return result;
        }

        static MyScoreResult HydrateMyScore(JSONObject json)
        {
            var r = new MyScoreResult();
            if (json == null) return r;

            var scoreField = json.GetField("score");
            r.hasScore = scoreField != null && scoreField.type != JSONObject.Type.Null;
            if (r.hasScore) r.score = scoreField.floatValue;

            var rankField = json.GetField("rank");
            r.hasRank = rankField != null && rankField.type != JSONObject.Type.Null;
            if (r.hasRank) r.rank = (int)rankField.floatValue;

            r.total = (int)(json.GetField("total")?.floatValue ?? 0);
            r.updatedAtIso = json.GetField("updatedAt")?.stringValue;
            return r;
        }

        // ── Lua bridge ───────────────────────────────────────────────────
        // A GLOBAL `dp.score` namespace — see the header comment for why
        // this doesn't add a per-script auto-resolving local the way
        // `storage` does. Same IL2CPP/Quest-portability reasoning as
        // ProfileAPI: results cross into Lua as plain tables, never as
        // custom C# types, so no XLua wrap generation is required.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua()
        {
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;

                env.Global.Set("dp_score_submit", new Action<string, string, double, string, string, Action<bool, LuaTable>>(
                    (contentId, metric, value, attraction, direction, cb) =>
                {
                    var dir = direction == "asc" ? ScoreDirection.Ascending : ScoreDirection.Descending;
                    SubmitScore(contentId, metric, value, attraction, dir, (ok, result) =>
                    {
                        if (cb == null) return;
                        try { cb.Invoke(ok, SubmitResultToLuaTable(env, result)); }
                        catch (Exception e) { Debug.LogWarning($"[ScoreAPI] submit callback threw: {e}"); }
                    });
                }));

                env.Global.Set("dp_score_fetch_mine", new Action<string, string, string, bool, string, Action<bool, LuaTable>>(
                    (contentId, metric, attraction, thisParkOnly, window, cb) =>
                {
                    FetchMyScore(contentId, metric, attraction, thisParkOnly, WindowFromWire(window), (ok, result) =>
                    {
                        try { cb?.Invoke(ok, MyScoreToLuaTable(env, result)); }
                        catch (Exception e) { Debug.LogWarning($"[ScoreAPI] fetchMine callback threw: {e}"); }
                    });
                }));

                env.Global.Set("dp_score_fetch_leaderboard", new Action<string, string, string, bool, bool, int, string, bool, string, Action<bool, LuaTable>>(
                    (contentId, metric, attraction, thisParkOnly, friendsOnly, limit, cursor, includeMe, window, cb) =>
                {
                    FetchLeaderboard(contentId, metric, attraction, thisParkOnly, friendsOnly, limit, cursor, includeMe,
                        WindowFromWire(window), (ok, result) =>
                    {
                        try { cb?.Invoke(ok, LeaderboardResultToLuaTable(env, result)); }
                        catch (Exception e) { Debug.LogWarning($"[ScoreAPI] fetchLeaderboard callback threw: {e}"); }
                    });
                }));

                env.DoString(@"
                    dp = dp or {}
                    dp.score = {
                        submit = function(contentId, metric, value, attraction, direction, cb)
                            dp_score_submit(contentId, metric, value, attraction, direction or 'desc', cb)
                        end,
                        fetchMine = function(contentId, metric, opts, cb)
                            opts = opts or {}
                            dp_score_fetch_mine(contentId, metric, opts.attraction, opts.thisParkOnly or false,
                                opts.window or 'all', cb)
                        end,
                        fetchLeaderboard = function(contentId, metric, opts, cb)
                            opts = opts or {}
                            dp_score_fetch_leaderboard(contentId, metric, opts.attraction, opts.thisParkOnly or false,
                                opts.friendsOnly or false, opts.limit or 25, opts.cursor, opts.includeMe ~= false,
                                opts.window or 'all', cb)
                        end,
                    }
                ", "dp.score.bootstrap");
            }
            catch (Exception e)
            {
                Debug.LogError("[ScoreAPI] Failed to register Lua bridge: " + e);
            }
        }

        // ── C# → LuaTable marshallers ────────────────────────────────────

        static LuaTable BoardResultToLuaTable(XLua.LuaEnv env, ScoreBoardResult b)
        {
            if (b == null) return null;
            var t = env.NewTable();
            t.Set("best", b.best);
            t.Set("at", b.atIso);
            t.Set("hasPreviousBest", b.hasPreviousBest);
            t.Set("previousBest", b.previousBest);
            t.Set("improved", b.improved);
            return t;
        }

        static LuaTable SubmitResultToLuaTable(XLua.LuaEnv env, ScoreSubmitResult r)
        {
            var t = env.NewTable();
            if (r == null) return t;
            t.Set("contentId", r.contentId);
            t.Set("attraction", r.attraction);
            t.Set("metric", r.metric);
            t.Set("accepted", r.accepted);
            t.Set("submittedValue", r.submittedValue);
            t.Set("submittedAt", r.submittedAtIso);
            t.Set("global", BoardResultToLuaTable(env, r.global));
            t.Set("park", BoardResultToLuaTable(env, r.park));
            t.Set("userId", r.userId);
            t.Set("dreamId", r.dreamId);
            return t;
        }

        static LuaTable LeaderboardEntryToLuaTable(XLua.LuaEnv env, LeaderboardEntry e)
        {
            var t = env.NewTable();
            t.Set("rank", e.rank);
            t.Set("userId", e.userId);
            t.Set("dreamId", e.dreamId);
            t.Set("displayName", e.displayName);
            t.Set("avatarUrl", e.avatarUrl);
            t.Set("score", e.score);
            t.Set("updatedAt", e.updatedAtIso);
            t.Set("isMe", e.isMe);
            return t;
        }

        static LuaTable LeaderboardResultToLuaTable(XLua.LuaEnv env, LeaderboardResult r)
        {
            var t = env.NewTable();
            if (r == null) return t;
            var entries = env.NewTable();
            for (int i = 0; i < r.entries.Count; i++) entries.Set(i + 1, LeaderboardEntryToLuaTable(env, r.entries[i]));
            t.Set("entries", entries);
            t.Set("nextCursor", r.nextCursor);
            t.Set("total", r.total);
            if (r.me != null)
            {
                var me = env.NewTable();
                me.Set("rank", r.me.rank);
                me.Set("score", r.me.score);
                me.Set("updatedAt", r.me.updatedAtIso);
                t.Set("me", me);
            }
            return t;
        }

        static LuaTable MyScoreToLuaTable(XLua.LuaEnv env, MyScoreResult r)
        {
            var t = env.NewTable();
            if (r == null) return t;
            t.Set("hasScore", r.hasScore);
            t.Set("score", r.score);
            t.Set("hasRank", r.hasRank);
            t.Set("rank", r.rank);
            t.Set("total", r.total);
            t.Set("updatedAt", r.updatedAtIso);
            return t;
        }
    }
}
