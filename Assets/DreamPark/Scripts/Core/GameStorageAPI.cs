// ─────────────────────────────────────────────────────────────────────────
//  GameStorageAPI.cs
//
//  DreamPark SDK — per-user game/attraction key-value storage
//
//  Lightweight per-user storage games read/write from Lua (or C#): high
//  scores on an arcade attraction, progress flags in an adventure game,
//  coin counts, checkpoints. Backed by /app/profile/storage/{contentId}
//  on the backend (same identity/auth model as ProfileAPI — headset
//  binding in production, preview key in SDK editor testing).
//
//  Spec: dreampark-core Docs/Game-Storage-Spec.md.
//
//  Model:
//    - TWO scopes per game: "game" (shared across the game's attractions)
//      and per-attraction buckets keyed by the slug of the attraction's
//      GameArea.resourceName (same slug as the backend attractions catalog).
//    - Reads are SYNCHRONOUS from a per-contentId cache — gameplay never
//      needs a callback to read a high score. The cache loads lazily on
//      first access once an identity is bound.
//    - Writes are OPS (set/delete/increment/max/min), applied optimistically
//      to the local cache and queued. The queue coalesces (per-frame
//      increment spam becomes ONE op) and flushes as a single batched POST
//      after a short debounce. max/min/increment are never converted into
//      set, so cross-device merge semantics survive batching:
//      max(high_score, 50) from this device can never clobber an 80
//      another device wrote.
//    - Guest play just works: unbound sessions read/write the local cache
//      and the op queue holds. When an identity binds mid-session the
//      server doc is fetched and queued ops REPLAY on top — guest progress
//      merges into the account automatically.
//    - HARD CAPS (server-authoritative, mirrored here): 64 keys / 8 KB per
//      scope, values are string(≤1 KB)/number/bool only. A game cannot
//      bomb a user's data schema.
//    - NO local disk persistence — shared venue headsets (same policy as
//      AuthAPI session tokens).
//
//  Calling pattern (C#):
//      GameStorageAPI.Max("WizardsWay", areaResourceName, "high_score", score);
//      var hs = GameStorageAPI.Get("WizardsWay", areaResourceName, "high_score");
//
//  Calling pattern (Lua) — every LuaBehaviour gets a `storage` variable
//  auto-bound to its owning attraction (lazy-resolved via GameArea /
//  PropTemplate / LevelTemplate up the hierarchy):
//      storage.increment("coins", 5)
//      storage.max("high_score", score)
//      storage.game.set("progress", 3)
//      storage.onReady(function() best = storage.get("high_score", 0) end)
//  Scripts outside any attraction (e.g. Player.prefab park systems):
//      local ws = dp.storage.game("WizardsWay")
// ─────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Defective.JSON;
using UnityEngine;
using XLua;

#if UNITY_EDITOR
using Unity.EditorCoroutines.Editor;
#endif

namespace DreamPark.API
{
    [LuaCallCSharp]
    public static class GameStorageAPI
    {
        // ── Caps (client mirror — the server is authoritative) ──────────
        public const int MaxKeyLength        = 64;
        public const int MaxStringValueBytes = 1024;
        public const int MaxKeysPerScope     = 64;
        public const int MaxAttractionScopes = 64;
        public const int MaxScopeBytes       = 8 * 1024;
        public const int MaxOpsPerRequest    = 32;

        const float FlushDebounceSeconds = 2f;
        const float FlushRetrySeconds    = 5f;
        const float FetchRetrySeconds    = 10f;
        const int   MaxFetchRetries      = 3;
        const int   QueueWarnThreshold   = 256; // coalescing keeps real queues tiny — big = bug in the game

        static readonly Regex KeyRe = new Regex("^[A-Za-z0-9_-]{1,64}$");

        // ── State ────────────────────────────────────────────────────────
        class PendingOp
        {
            public string attraction; // slug, or null/"" for game scope
            public string key;
            public string op;         // set | delete | increment | max | min
            public object value;      // string | bool | double (null for delete)
        }

        class ContentStore
        {
            public string contentId;
            public int    epoch;      // identity generation — see _epoch below
            public bool   loaded;
            public bool   fetchInFlight;
            public bool   fetchRetryScheduled;
            public int    fetchRetries;
            public bool   flushScheduled;
            public bool   flushInFlight;
            // scopes[""] = game scope; scopes[slug] = attraction bucket.
            public readonly Dictionary<string, Dictionary<string, object>> scopes = new Dictionary<string, Dictionary<string, object>>();
            public readonly List<PendingOp> queue      = new List<PendingOp>();
            public readonly List<Action>    readyQueue = new List<Action>();
        }

        static readonly Dictionary<string, ContentStore> _stores = new Dictionary<string, ContentStore>();

        // Bumped on OnIdentityCleared. Scheduled coroutines and in-flight
        // request callbacks hold direct ContentStore references that outlive
        // a _stores.Clear() — every deferred network step re-checks
        // `store.epoch == _epoch` so player A's queued ops can never flush
        // under player B's freshly-bound identity. (OnIdentityBound does
        // NOT bump it: keeping the stores/queue across an unbound→bound
        // transition is the guest-merge feature.)
        static int _epoch;

        /// <summary>Fired when a game's server snapshot has been loaded (or
        /// re-loaded after an identity bind). Arg = contentId.</summary>
        public static event Action<string> OnStorageSynced;

        /// <summary>Fired after every LOCAL numeric write op with the post-op
        /// value: (contentId, attractionSlug ("" = game scope), key, op
        /// (increment|max|min), amount, newLocalValue). Observers (e.g. the
        /// AdventureLedger, which folds "max" ops into score_set timeline
        /// events) can watch score writes without wrapping the API. Fires
        /// per CALL (pre-coalesce) — subscribers must do their own folding
        /// if a game spams writes per frame.</summary>
        public static event Action<string, string, string, string, double, double> OnNumericOp;

        /// <summary>Fired after every LOCAL Set(): (contentId, attractionSlug
        /// ("" = game scope), key, value (string|bool|double)). Together with
        /// OnNumericOp this gives observers the full write stream — the
        /// AdventureLedger folds it into per-visit "game data saved" rows.
        /// Same pre-coalesce caveat as OnNumericOp.</summary>
        public static event Action<string, string, string, object> OnSetOp;

        // ── Scope key helpers ────────────────────────────────────────────

        /// <summary>Slug an attraction identifier the SAME way the backend
        /// does (lib/parkMapService.slugifyResourceName): lowercase, every
        /// run of non-alphanumerics → "__", trim underscores, cap 200.
        /// Idempotent — accepts a raw GameArea.resourceName or a pre-slugged
        /// key. Null/empty → "" (the game scope).</summary>
        public static string Slugify(string attraction)
        {
            if (string.IsNullOrEmpty(attraction)) return "";
            var s = attraction.Trim().ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9]+", "__");
            s = s.Trim('_');
            if (s.Length > 200) s = s.Substring(0, 200).Trim('_'); // fixpoint of the server's slugify
            return s;
        }

        // ── Public API — reads (synchronous, from cache) ─────────────────

        /// <summary>Read one value. attraction = GameArea.resourceName (or
        /// pre-slugged), null for the game scope. Returns string/bool/double
        /// or null when unset. Triggers a lazy fetch on first touch.</summary>
        public static object Get(string contentId, string attraction, string key)
        {
            var store = EnsureStore(contentId);
            if (store == null || string.IsNullOrEmpty(key)) return null;
            var bucket = GetBucket(store, Slugify(attraction), create: false);
            if (bucket == null) return null;
            return bucket.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>True once the game's server snapshot has loaded. Reads
        /// work before that (local cache / defaults) — this only tells you
        /// whether persisted values have arrived.</summary>
        public static bool IsReady(string contentId)
        {
            var store = EnsureStore(contentId);
            return store != null && store.loaded;
        }

        /// <summary>Fires once the game's snapshot is loaded — immediately
        /// if it already is. Also fires (with local-only data) if the fetch
        /// exhausts its retries, so gameplay never hangs on it.</summary>
        public static void OnReady(string contentId, Action callback)
        {
            if (callback == null) return;
            var store = EnsureStore(contentId);
            if (store == null) return;
            // Fire immediately when loaded — or when the fetch already
            // exhausted its retries (nothing will refire until the next
            // identity bind; the caller proceeds on local data).
            if (store.loaded || (!store.fetchInFlight && !store.fetchRetryScheduled && store.fetchRetries >= MaxFetchRetries))
            {
                try { callback(); } catch (Exception e) { Debug.LogWarning($"[GameStorageAPI] onReady callback threw: {e}"); }
                return;
            }
            store.readyQueue.Add(callback);
        }

        // ── Public API — writes (optimistic local + queued op) ───────────

        /// <summary>Absolute write. Value must be a string (≤1 KB UTF-8),
        /// finite number, or bool. Returns false (with a warning) when the
        /// key/value is invalid or a cap would be exceeded — the write is
        /// rejected BEFORE it queues, mirroring the server's caps.</summary>
        public static bool Set(string contentId, string attraction, string key, object value)
        {
            var store = EnsureStore(contentId);
            if (store == null) return false;
            if (!ValidateKey(key)) return false;
            if (!TryNormalize(value, out var norm))
            {
                Debug.LogWarning($"[GameStorageAPI] Set('{key}') rejected — values must be a string (≤{MaxStringValueBytes} bytes), finite number, or bool.");
                return false;
            }
            var slug = Slugify(attraction);
            var bucket = GetBucketForWrite(store, slug, key);
            if (bucket == null) return false;

            // Mirror the server's per-scope byte cap (conservative estimate)
            // so a string-heavy game fails THIS write loudly instead of
            // 413ing whole batches forever server-side.
            int estimate = EstimateScopeBytes(bucket);
            if (bucket.TryGetValue(key, out var old)) estimate -= EntryBytes(key, old);
            estimate += EntryBytes(key, norm);
            if (estimate > MaxScopeBytes)
            {
                Debug.LogWarning($"[GameStorageAPI] Set('{key}') rejected — scope '{(slug.Length == 0 ? "game" : slug)}' would exceed ~{MaxScopeBytes} bytes.");
                return false;
            }

            bucket[key] = norm;
            Enqueue(store, slug, key, "set", norm);
            try { OnSetOp?.Invoke(contentId, slug, key, norm); }
            catch (Exception e) { Debug.LogWarning($"[GameStorageAPI] OnSetOp subscriber threw: {e}"); }
            return true;
        }

        /// <summary>existing + amount (missing/non-numeric existing counts
        /// as 0). Returns the new local value.</summary>
        public static double Increment(string contentId, string attraction, string key, double amount = 1)
        {
            return NumericOp(contentId, attraction, key, "increment", amount);
        }

        /// <summary>Set-if-greater — THE high-score primitive. Race-safe
        /// across devices (the server applies max, not a blind write).
        /// Returns the new local value.</summary>
        public static double Max(string contentId, string attraction, string key, double value)
        {
            return NumericOp(contentId, attraction, key, "max", value);
        }

        /// <summary>Set-if-lower (best lap time). Returns the new local value.</summary>
        public static double Min(string contentId, string attraction, string key, double value)
        {
            return NumericOp(contentId, attraction, key, "min", value);
        }

        public static void Delete(string contentId, string attraction, string key)
        {
            var store = EnsureStore(contentId);
            if (store == null || !ValidateKey(key)) return;
            var slug = Slugify(attraction);
            var bucket = GetBucket(store, slug, create: false);
            bucket?.Remove(key);
            Enqueue(store, slug, key, "delete", null);
        }

        /// <summary>Flush every pending write NOW (bypasses the debounce).
        /// ProfileAPI.ClearIdentity() calls this automatically as a
        /// best-effort final push before wiping the queue — once the
        /// binding is released the backend can no longer resolve the
        /// identity. Games with critical writes (run just ended) can also
        /// call it directly at natural checkpoints.</summary>
        public static void FlushAll()
        {
            foreach (var store in _stores.Values) Flush(store);
        }

        static double NumericOp(string contentId, string attraction, string key, string opName, double amount)
        {
            var store = EnsureStore(contentId);
            if (store == null || !ValidateKey(key)) return 0;
            if (double.IsNaN(amount) || double.IsInfinity(amount))
            {
                Debug.LogWarning($"[GameStorageAPI] {opName}('{key}') rejected — amount must be finite.");
                return 0;
            }
            var slug = Slugify(attraction);
            var bucket = GetBucketForWrite(store, slug, key);
            if (bucket == null) return 0;

            double cur = bucket.TryGetValue(key, out var existing) && existing is double d ? d : double.NaN;
            double next;
            switch (opName)
            {
                case "increment": next = (double.IsNaN(cur) ? 0 : cur) + amount; break;
                case "max":       next = double.IsNaN(cur) ? amount : Math.Max(cur, amount); break;
                default:          next = double.IsNaN(cur) ? amount : Math.Min(cur, amount); break;
            }
            bucket[key] = next;
            Enqueue(store, slug, key, opName, amount);
            try { OnNumericOp?.Invoke(contentId, slug, key, opName, amount, next); }
            catch (Exception e) { Debug.LogWarning($"[GameStorageAPI] OnNumericOp subscriber threw: {e}"); }
            return next;
        }

        // ── Store / bucket plumbing ──────────────────────────────────────

        static ContentStore EnsureStore(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            if (!_stores.TryGetValue(contentId, out var store))
            {
                store = new ContentStore { contentId = contentId, epoch = _epoch };
                _stores[contentId] = store;
                HookIdentity();
            }
            if (!store.loaded && !store.fetchInFlight && !store.fetchRetryScheduled
                && store.fetchRetries < MaxFetchRetries && ProfileAPI.IsBound)
            {
                Fetch(store);
            }
            return store;
        }

        static Dictionary<string, object> GetBucket(ContentStore store, string slug, bool create)
        {
            if (store.scopes.TryGetValue(slug, out var bucket)) return bucket;
            if (!create) return null;
            bucket = new Dictionary<string, object>();
            store.scopes[slug] = bucket;
            return bucket;
        }

        // Bucket lookup for a write — mirrors the server's key-count and
        // attraction-scope-count caps so bad writes fail fast and loudly
        // instead of 413ing a whole batch later.
        static Dictionary<string, object> GetBucketForWrite(ContentStore store, string slug, string key)
        {
            if (!store.scopes.ContainsKey(slug) && slug.Length > 0)
            {
                int attractionScopes = 0;
                foreach (var k in store.scopes.Keys) if (k.Length > 0) attractionScopes++;
                if (attractionScopes >= MaxAttractionScopes)
                {
                    Debug.LogWarning($"[GameStorageAPI] write rejected — at most {MaxAttractionScopes} attraction scopes per game.");
                    return null;
                }
            }
            var bucket = GetBucket(store, slug, create: true);
            if (!bucket.ContainsKey(key) && bucket.Count >= MaxKeysPerScope)
            {
                Debug.LogWarning($"[GameStorageAPI] write rejected — at most {MaxKeysPerScope} keys per scope (scope '{(slug.Length == 0 ? "game" : slug)}').");
                return null;
            }
            return bucket;
        }

        static bool ValidateKey(string key)
        {
            if (!string.IsNullOrEmpty(key) && KeyRe.IsMatch(key)) return true;
            Debug.LogWarning($"[GameStorageAPI] invalid key '{key}' — keys must match [A-Za-z0-9_-]{{1,{MaxKeyLength}}}.");
            return false;
        }

        static bool TryNormalize(object value, out object norm)
        {
            norm = null;
            switch (value)
            {
                case bool b:   norm = b; return true;
                case string s: if (Encoding.UTF8.GetByteCount(s) > MaxStringValueBytes) return false; norm = s; return true;
                case double d: if (double.IsNaN(d) || double.IsInfinity(d)) return false; norm = d; return true;
                case float f:  if (float.IsNaN(f) || float.IsInfinity(f)) return false; norm = (double)f; return true;
                case int i:    norm = (double)i; return true;
                case long l:   norm = (double)l; return true;
                case short sh: norm = (double)sh; return true;
                case byte by:  norm = (double)by; return true;
            }
            return false;
        }

        static double ToDouble(object v) => v is double d ? d : 0;

        // Conservative serialized-size estimate (keys + values + JSON
        // punctuation) mirroring the server's MAX_SCOPE_BYTES — slightly
        // over-counts on purpose so the server cap can't be hit first.
        static int EntryBytes(string key, object value)
        {
            int n = Encoding.UTF8.GetByteCount(key) + 6; // quotes, colon, comma
            n += value is string s ? Encoding.UTF8.GetByteCount(s) + 2 : 24;
            return n;
        }

        static int EstimateScopeBytes(Dictionary<string, object> bucket)
        {
            int total = 2;
            foreach (var kv in bucket) total += EntryBytes(kv.Key, kv.Value);
            return total;
        }

        // ── Op queue + coalescing ────────────────────────────────────────
        // Coalescing keeps the queue bounded (per-frame increment spam is
        // ONE op) while PRESERVING op semantics: increment/max/min are never
        // rewritten into set (that would break cross-device merges), but a
        // pending set absorbs later numeric ops (set is absolute — the local
        // cache already holds the folded result).

        static void Enqueue(ContentStore store, string attraction, string key, string opName, object value)
        {
            var queue = store.queue;

            if (opName == "set" || opName == "delete")
            {
                // Absolute — everything queued for this key before now is moot.
                queue.RemoveAll(o => o.attraction == attraction && o.key == key);
                queue.Add(new PendingOp { attraction = attraction, key = key, op = opName, value = value });
            }
            else
            {
                int last = -1;
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    if (queue[i].attraction == attraction && queue[i].key == key) { last = i; break; }
                }
                var p = last >= 0 ? queue[last] : null;
                if (p != null && p.op == opName)
                {
                    // increment+increment sums; max+max keeps larger; min+min smaller.
                    double a = ToDouble(p.value), b = ToDouble(value);
                    p.value = opName == "increment" ? a + b : opName == "max" ? Math.Max(a, b) : Math.Min(a, b);
                }
                else if (p != null && p.op == "set")
                {
                    // Fold into the absolute set — the local cache already
                    // holds the post-op value, which is exactly what the
                    // absolute write should now carry.
                    var bucket = GetBucket(store, attraction, create: false);
                    if (bucket != null && bucket.TryGetValue(key, out var local)) p.value = local;
                }
                else
                {
                    queue.Add(new PendingOp { attraction = attraction, key = key, op = opName, value = value });
                }
            }

            if (queue.Count > QueueWarnThreshold)
            {
                Debug.LogWarning($"[GameStorageAPI] {store.contentId} op queue is large ({queue.Count}) — is something writing unique keys every frame?");
            }
            ScheduleFlush(store, FlushDebounceSeconds);
        }

        // ── Networking — fetch ───────────────────────────────────────────

        static void Fetch(ContentStore store)
        {
            if (!ProfileAPI.IsBound || store.epoch != _epoch) return;
            store.fetchInFlight = true;

            var qs = ProfileAPI.Source == ProfileAPI.ProfileSource.Headset
                ? "?headsetId=" + Uri.EscapeDataString(SystemInfo.deviceUniqueIdentifier)
                : "";
            DreamParkAPI.GET($"/app/profile/storage/{Uri.EscapeDataString(store.contentId)}{qs}", ProfileAPI.AuthHeader(), (ok, resp) =>
            {
                store.fetchInFlight = false;
                if (store.epoch != _epoch) return; // identity changed mid-request
                bool success = ok && resp?.json != null && (resp.json.GetField("success")?.boolValue ?? false);
                if (!success)
                {
                    store.fetchRetries++;
                    Debug.LogWarning($"[GameStorageAPI] fetch '{store.contentId}' failed ({resp?.error ?? "no response"}) — attempt {store.fetchRetries}/{MaxFetchRetries}.");
                    if (store.fetchRetries < MaxFetchRetries)
                    {
                        store.fetchRetryScheduled = true;
                        RunRoutine(RetryFetchAfter(store, FetchRetrySeconds));
                    }
                    else
                    {
                        // Give up until the next identity bind — flush the
                        // ready queue so gameplay proceeds on local data.
                        FlushReadyQueue(store);
                    }
                    return;
                }

                store.fetchRetries = 0;
                HydrateFromServer(store, resp.json);

                // Replay pending local ops on top of the server snapshot —
                // writes made while unbound (guest play) or while this fetch
                // was in flight stay visible locally AND still flush.
                foreach (var op in store.queue) ApplyLocal(store, op);

                store.loaded = true;
                FlushReadyQueue(store);
                try { OnStorageSynced?.Invoke(store.contentId); } catch (Exception e) { Debug.LogWarning($"[GameStorageAPI] OnStorageSynced subscriber threw: {e}"); }
                if (store.queue.Count > 0) ScheduleFlush(store, 0f);
            });
        }

        static IEnumerator RetryFetchAfter(ContentStore store, float seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) yield return null;
            store.fetchRetryScheduled = false;
            if (store.epoch != _epoch) yield break; // identity changed while waiting
            Fetch(store);
        }

        static void HydrateFromServer(ContentStore store, JSONObject json)
        {
            store.scopes.Clear();
            var storage = json.GetField("storage");
            if (storage == null) return;

            var game = storage.GetField("game");
            if (game != null && game.type == JSONObject.Type.Object)
            {
                store.scopes[""] = JsonMapToDict(game);
            }
            var attractions = storage.GetField("attractions");
            if (attractions != null && attractions.type == JSONObject.Type.Object && attractions.keys != null)
            {
                for (int i = 0; i < attractions.keys.Count; i++)
                {
                    var bucket = attractions.list[i];
                    if (bucket != null && bucket.type == JSONObject.Type.Object)
                        store.scopes[attractions.keys[i]] = JsonMapToDict(bucket);
                }
            }
        }

        static Dictionary<string, object> JsonMapToDict(JSONObject obj)
        {
            var dict = new Dictionary<string, object>();
            if (obj?.keys == null) return dict;
            for (int i = 0; i < obj.keys.Count; i++)
            {
                var v = FromJsonValue(obj.list[i]);
                if (v != null) dict[obj.keys[i]] = v;
            }
            return dict;
        }

        // Integers ride longValue (exact to 2^53 server-side); fractional
        // values come through the parser's float path.
        static object FromJsonValue(JSONObject v)
        {
            if (v == null) return null;
            switch (v.type)
            {
                case JSONObject.Type.String: return v.stringValue;
                case JSONObject.Type.Bool:   return v.boolValue;
                case JSONObject.Type.Number: return v.isInteger ? (double)v.longValue : (double)v.floatValue;
                default: return null;
            }
        }

        static void ApplyLocal(ContentStore store, PendingOp op)
        {
            var bucket = GetBucket(store, op.attraction ?? "", create: op.op != "delete");
            if (bucket == null) return;
            switch (op.op)
            {
                case "set":    bucket[op.key] = op.value; break;
                case "delete": bucket.Remove(op.key); break;
                default:
                    double cur = bucket.TryGetValue(op.key, out var existing) && existing is double d ? d : double.NaN;
                    double amount = ToDouble(op.value);
                    if (op.op == "increment")   bucket[op.key] = (double.IsNaN(cur) ? 0 : cur) + amount;
                    else if (op.op == "max")    bucket[op.key] = double.IsNaN(cur) ? amount : Math.Max(cur, amount);
                    else /* min */              bucket[op.key] = double.IsNaN(cur) ? amount : Math.Min(cur, amount);
                    break;
            }
        }

        // ── Networking — flush ───────────────────────────────────────────

        static void ScheduleFlush(ContentStore store, float delay)
        {
            if (store.flushScheduled) return;
            store.flushScheduled = true;
            RunRoutine(FlushAfter(store, delay));
        }

        static IEnumerator FlushAfter(ContentStore store, float seconds)
        {
            // Manual timer (not WaitForSeconds) so the same coroutine works
            // under EditorCoroutineUtility in the editor.
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) yield return null;
            store.flushScheduled = false;
            if (store.epoch != _epoch) yield break; // identity changed while waiting
            Flush(store);
        }

        static void Flush(ContentStore store)
        {
            if (store.epoch != _epoch) return; // stale store — never flush player A's ops as player B
            if (store.flushInFlight || store.queue.Count == 0) return;
            if (!ProfileAPI.IsBound) return; // stays queued; replays after bind + fetch

            int n = Math.Min(store.queue.Count, MaxOpsPerRequest);
            var chunk = store.queue.GetRange(0, n);
            store.queue.RemoveRange(0, n);
            store.flushInFlight = true;

            var body = new JSONObject(JSONObject.Type.Object);
            if (ProfileAPI.Source == ProfileAPI.ProfileSource.Headset)
                body.AddField("headsetId", SystemInfo.deviceUniqueIdentifier);
            var opsArr = new JSONObject(JSONObject.Type.Array);
            foreach (var op in chunk)
            {
                var o = new JSONObject(JSONObject.Type.Object);
                if (!string.IsNullOrEmpty(op.attraction)) o.AddField("attraction", op.attraction);
                o.AddField("key", op.key);
                o.AddField("op", op.op);
                if (op.op != "delete") AddValueField(o, "value", op.value);
                opsArr.Add(o);
            }
            body.AddField("ops", opsArr);

            DreamParkAPI.POST($"/app/profile/storage/{Uri.EscapeDataString(store.contentId)}", ProfileAPI.AuthHeader(), body, (ok, resp) =>
            {
                store.flushInFlight = false;
                bool success = ok && (resp?.json?.GetField("success")?.boolValue ?? false);
                if (success)
                {
                    Reconcile(store, resp.json.GetField("results"));
                    if (store.queue.Count > 0) ScheduleFlush(store, 0.05f); // drain remainder
                    return;
                }

                if (store.epoch != _epoch) return; // identity changed mid-request — drop silently

                long code = resp?.statusCode ?? 0;
                if (code == 400 || code == 413)
                {
                    // Validation / cap rejection — the batch can NEVER
                    // succeed, so retrying would loop forever. Drop it —
                    // but keep draining anything queued behind it.
                    // (Client-side cap mirrors should make this rare.)
                    Debug.LogWarning($"[GameStorageAPI] '{store.contentId}' write rejected ({code}): {resp?.rawText}. Batch dropped.");
                    if (store.queue.Count > 0) ScheduleFlush(store, FlushDebounceSeconds);
                }
                else
                {
                    // Transient (network / 5xx) — requeue at the head so op
                    // order is preserved, retry later.
                    store.queue.InsertRange(0, chunk);
                    Debug.LogWarning($"[GameStorageAPI] flush '{store.contentId}' failed ({resp?.error ?? code.ToString()}) — retrying in {FlushRetrySeconds}s.");
                    ScheduleFlush(store, FlushRetrySeconds);
                }
            });
        }

        // Apply the server's authoritative post-op values, UNLESS newer
        // local ops are pending on that key (they'd re-diverge us on
        // purpose — the next flush reconciles again).
        static void Reconcile(ContentStore store, JSONObject results)
        {
            if (results == null || results.type != JSONObject.Type.Array || results.list == null) return;
            foreach (var entry in results.list)
            {
                var key = entry.GetField("key")?.stringValue;
                if (string.IsNullOrEmpty(key)) continue;
                var attraction = entry.GetField("attraction")?.stringValue ?? "";

                bool hasPending = false;
                foreach (var op in store.queue)
                {
                    if ((op.attraction ?? "") == attraction && op.key == key) { hasPending = true; break; }
                }
                if (hasPending) continue;

                var serverValue = FromJsonValue(entry.GetField("value"));
                var bucket = GetBucket(store, attraction, create: serverValue != null);
                if (bucket == null) continue;
                if (serverValue == null) bucket.Remove(key);
                else bucket[key] = serverValue;
            }
        }

        static void AddValueField(JSONObject obj, string name, object v)
        {
            switch (v)
            {
                case string s: obj.AddField(name, s); break;
                case bool b:   obj.AddField(name, b); break;
                case double d:
                    // Integral values ship as JSON integers — exact through
                    // the backend (doubles are exact to 2^53); fractional
                    // values go through the double path.
                    if (d == Math.Floor(d) && Math.Abs(d) <= 9.007199254740991e15) obj.AddField(name, (long)d);
                    else obj.AddField(name, d);
                    break;
            }
        }

        // ── Identity lifecycle ───────────────────────────────────────────

        static bool _identityHooked;
        static void HookIdentity()
        {
            if (_identityHooked) return;
            _identityHooked = true;

            ProfileAPI.OnIdentityBound += () =>
            {
                // New identity — refetch everything we have cached. Pending
                // guest ops stay queued and replay onto the fresh snapshot
                // (guest progress merges into the account).
                foreach (var store in _stores.Values)
                {
                    store.loaded = false;
                    store.fetchRetries = 0;
                    if (!store.fetchInFlight && !store.fetchRetryScheduled) Fetch(store);
                }
            };

            ProfileAPI.OnIdentityCleared += () =>
            {
                // Session over — the next player must not inherit cache OR
                // queue. ProfileAPI.ClearIdentity() calls FlushAll() right
                // before firing this event (best-effort final push);
                // anything still queued here is gone by design (the backend
                // could no longer resolve the identity anyway). The epoch
                // bump invalidates every scheduled coroutine / in-flight
                // callback still holding a reference to an old store.
                _epoch++;
                _stores.Clear();
            };
        }

        static void FlushReadyQueue(ContentStore store)
        {
            if (store.readyQueue.Count == 0) return;
            var callbacks = store.readyQueue.ToArray();
            store.readyQueue.Clear();
            foreach (var cb in callbacks)
            {
                try { cb?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[GameStorageAPI] onReady callback threw: {e}"); }
            }
        }

        static void RunRoutine(IEnumerator routine)
        {
#if UNITY_EDITOR
            EditorCoroutineUtility.StartCoroutineOwnerless(routine);
#else
            CoroutineRunner.Run(routine);
#endif
        }

        // ── Scope resolution (for the per-script Lua `storage` proxy) ────
        // From any GameObject, resolve the owning {gameId, attraction slug}
        // by walking up to the stamped template components. GameArea first
        // (attractions always carry one, and it holds BOTH ids), then
        // PropTemplate, then LevelTemplate (gameId only).
        static LuaTable ResolveScopeTable(GameObject go)
        {
            if (go == null) return null;
            string gameId = null, resourceName = null;

            var area = go.GetComponentInParent<GameArea>(true);
            if (area != null) { gameId = area.gameId; resourceName = area.resourceName; }
            if (string.IsNullOrEmpty(gameId))
            {
                var prop = go.GetComponentInParent<PropTemplate>(true);
                if (prop != null) { gameId = prop.gameId; resourceName = prop.resourceName; }
            }
            if (string.IsNullOrEmpty(gameId))
            {
                var level = go.GetComponentInParent<LevelTemplate>(true);
                if (level != null) gameId = level.gameId;
            }
            if (string.IsNullOrEmpty(gameId)) return null;

            var t = LuaBehaviour.GetLuaEnv().NewTable();
            t.Set("gameId", gameId);
            var slug = Slugify(resourceName);
            if (slug.Length > 0) t.Set("attraction", slug);
            return t;
        }

        static bool _warnedUnscoped;
        static void WarnUnscoped()
        {
            if (_warnedUnscoped) return;
            _warnedUnscoped = true;
            Debug.LogWarning("[GameStorageAPI] a Lua script used `storage` but has no GameArea/PropTemplate/LevelTemplate above it — calls are no-ops. Use dp.storage.game(gameId) for scripts outside attractions (warned once).");
        }

        // ── Lua bridge ───────────────────────────────────────────────────
        // Flat context-free globals + an ergonomic bootstrap. LuaBehaviour
        // injects a per-script `storage` proxy via __dp_storage_lazy — lazy
        // because the park spawner parents spawned content AFTER Awake
        // (same reason net_send reads netId.Id at send time).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua()
        {
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;

                env.Global.Set("dp_storage_get",       new Func<string, string, string, object>((cid, attr, key) => Get(cid, attr, key)));
                env.Global.Set("dp_storage_set",       new Func<string, string, string, object, bool>((cid, attr, key, v) => Set(cid, attr, key, v)));
                env.Global.Set("dp_storage_increment", new Func<string, string, string, double, double>((cid, attr, key, n) => Increment(cid, attr, key, n)));
                env.Global.Set("dp_storage_max",       new Func<string, string, string, double, double>((cid, attr, key, v) => Max(cid, attr, key, v)));
                env.Global.Set("dp_storage_min",       new Func<string, string, string, double, double>((cid, attr, key, v) => Min(cid, attr, key, v)));
                env.Global.Set("dp_storage_delete",    new Action<string, string, string>((cid, attr, key) => Delete(cid, attr, key)));
                env.Global.Set("dp_storage_is_ready",  new Func<string, bool>(IsReady));
                env.Global.Set("dp_storage_on_ready",  new Action<string, Action>((cid, cb) => OnReady(cid, cb)));
                env.Global.Set("dp_storage_resolve",   new Func<GameObject, LuaTable>(ResolveScopeTable));
                env.Global.Set("dp_storage_warn_unscoped", new Action(WarnUnscoped));

                env.DoString(@"
                    dp = dp or {}

                    -- bound accessor for one (gameId, attraction) scope
                    function __dp_storage_build(gameId, attraction)
                        local s = {}
                        s.get = function(key, default)
                            local v = dp_storage_get(gameId, attraction, key)
                            if v == nil then return default end
                            return v
                        end
                        s.set       = function(key, value)  return dp_storage_set(gameId, attraction, key, value) end
                        s.increment = function(key, amount) return dp_storage_increment(gameId, attraction, key, amount or 1) end
                        s.max       = function(key, value)  return dp_storage_max(gameId, attraction, key, value or 0) end
                        s.min       = function(key, value)  return dp_storage_min(gameId, attraction, key, value or 0) end
                        s.delete    = function(key)         dp_storage_delete(gameId, attraction, key) end
                        s.isReady   = function()            return dp_storage_is_ready(gameId) end
                        s.onReady   = function(cb)          dp_storage_on_ready(gameId, cb) end
                        s.gameId     = gameId
                        s.attraction = attraction
                        return s
                    end

                    -- warn-once no-op fallback for scripts with no resolvable scope
                    __dp_storage_unscoped = {
                        get       = function(key, default) dp_storage_warn_unscoped() return default end,
                        set       = function() dp_storage_warn_unscoped() return false end,
                        increment = function() dp_storage_warn_unscoped() return 0 end,
                        max       = function() dp_storage_warn_unscoped() return 0 end,
                        min       = function() dp_storage_warn_unscoped() return 0 end,
                        delete    = function() dp_storage_warn_unscoped() end,
                        isReady   = function() return false end,
                        onReady   = function() dp_storage_warn_unscoped() end,
                    }
                    __dp_storage_unscoped.game = __dp_storage_unscoped

                    -- lazy per-script proxy: resolves the owning attraction on
                    -- FIRST USE (spawn-time parenting happens after Awake, so
                    -- resolving eagerly could freeze a nil scope). Resolution
                    -- failure is not memoized — it retries on later access.
                    function __dp_storage_lazy(go)
                        local built = nil
                        return setmetatable({}, { __index = function(_, k)
                            if built == nil then
                                local info = dp_storage_resolve(go)
                                if info == nil then return __dp_storage_unscoped[k] end
                                built = __dp_storage_build(info.gameId, info.attraction)
                                built.game = __dp_storage_build(info.gameId, nil)
                            end
                            return built[k]
                        end })
                    end

                    dp.storage = {
                        -- explicit game-scope accessor for scripts outside any
                        -- attraction (e.g. park-wide systems on Player.prefab)
                        game = function(gameId) return __dp_storage_build(gameId, nil) end,
                    }
                ", "dp.storage.bootstrap");
            }
            catch (Exception e)
            {
                Debug.LogError("[GameStorageAPI] Failed to register Lua bridge: " + e);
            }
        }
    }
}
