// ─────────────────────────────────────────────────────────────────────
//  PlaylistBundle.cs — the backend's copy of a playlist for a run (repair-oct20 CONTRACT § 5):
//    GET /app/playlists/:titleId/bundle?playlist=<id>   (device key; ETag → 304; Beta → base)
//    { playlist (the record: lua, dreamie, timing, rev, arc… stops' goal), block, goals {key: {lua, rev}},
//      missingGoals, dreamie, guide, revs, etag }
//  Online, the backend wins over the manifest's compiled copy (goal blocks register as "backend",
//  the record's lua / dreamie / timing / stop goals overlay the plan). Every bundle is cached on the
//  device (offline resume plays from it). The run is PINNED (record v2 § 8.9): once a run has started it
//  keeps the bundle it started with — record AND goal-block revs (PlaylistRun.ApplyBundle / PinnedGoal) —
//  and never refetches; library edits apply from the next run. rec.revs logs what it pinned.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    public static class PlaylistBundle
    {
        public static Func<string> Dir = () => Path.Combine(Application.persistentDataPath, "dreampark", "playlist-run", "bundles");
        /// (path, ifNoneMatch, done(ok, status, text, etag)) — default: DreamParkAPI.GET with the device key. Tests replace it.
        public static Action<string, string, Action<bool, long, string, string>> Get = (path, etag, done) =>
        {
            try
            {
                API.DreamParkAPI.GET(path, API.ProfileAPI.AuthHeader(), etag, (ok, r) => done(ok, r?.statusCode ?? 0, r?.rawText, r?.etag));
            }
            catch (Exception e) { Debug.LogWarning("[pl-runtime] bundle GET threw: " + e.Message); done(false, 0, null, null); }
        };
        public static Action<string> Log = m => Debug.Log(m);
        /// Seconds (the refetch throttle). Tests replace it.
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;
        /// The installed release of a title, sent as ?release= (pl-web: the backend then answers 409 RELEASE_TOO_OLD for a
        /// record whose goal blocks need newer content). ≤ 0 = unknown (editor / local builds): not sent, as before.
        public static Func<string, int> Release = id => { try { return PlaylistRunWorld.Main?.InstalledRelease?.Invoke(id) ?? -1; } catch (Exception) { return -1; } };

        // 409 RELEASE_TOO_OLD answers: key → the release the record needs (this session; the cached copy is dropped).
        static readonly Dictionary<string, int> s_tooOld = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// The release a playlist needs when the backend refused it for `installed` (409 RELEASE_TOO_OLD), else 0.
        public static int TooOld(string titleId, string playlistId, int installed)
        {
            if (installed <= 0) return 0;
            int need;
            lock (s_bundles) if (!s_tooOld.TryGetValue(Key(titleId, playlistId), out need)) return 0;
            return installed < need ? need : 0;
        }

        static readonly Dictionary<string, JSONObject> s_bundles = new Dictionary<string, JSONObject>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> s_fetching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, double> s_fetchedAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        static string Key(string titleId, string playlistId) => PlaylistGoalLibrary.BaseTitle(titleId) + "|" + (playlistId ?? "");
        static string FileFor(string titleId, string playlistId) =>
            Path.Combine(Dir(), (PlaylistGoalLibrary.BaseTitle(titleId) + "_" + (playlistId ?? "")).Replace('/', '_') + ".json");

        /// The cached bundle (memory, else disk), or null.
        public static JSONObject Cached(string titleId, string playlistId)
        {
            string k = Key(titleId, playlistId);
            lock (s_bundles) { if (s_tooOld.ContainsKey(k)) return null; if (s_bundles.TryGetValue(k, out var b)) return b; }
            try
            {
                string f = FileFor(titleId, playlistId);
                if (!File.Exists(f)) return null;
                var j = new JSONObject(File.ReadAllText(f));
                if (j.type != JSONObject.Type.Object) return null;
                Register(titleId, j);
                lock (s_bundles) s_bundles[k] = j;
                return j;
            }
            catch (Exception) { return null; }
        }

        /// A bundle arrived (or came from the cache): its goal blocks join the library as the BACKEND copy.
        public static void Register(string titleId, JSONObject bundle)
        {
            var goals = bundle?.GetField("goals");
            if (goals != null && goals.type == JSONObject.Type.Object) PlaylistGoalLibrary.RegisterBackend(PlaylistRunPlan.Str(bundle, "titleId", titleId), goals.Print());
        }

        /// Store a bundle (tests, the offline sim, a pushed copy).
        public static void Put(string titleId, string playlistId, JSONObject bundle, bool persist = true)
        {
            if (bundle == null) return;
            Register(titleId, bundle);
            lock (s_bundles) s_bundles[Key(titleId, playlistId)] = bundle;
            if (!persist) return;
            try { Directory.CreateDirectory(Dir()); File.WriteAllText(FileFor(titleId, playlistId), bundle.Print()); }
            catch (Exception e) { Log?.Invoke("[pl-runtime] bundle cache write failed: " + e.Message); }
        }

        /// Fetch (ETag) unless fetched in the last `minSeconds`; `done(bundle or null)` on the main thread.
        /// playlistId "" = the title's DEFAULT playlist (pl-store: block.defaultPlaylist, else the generated freeplay).
        public static void Ensure(string titleId, string playlistId, Action<JSONObject> done = null, double minSeconds = 60)
        {
            if (string.IsNullOrEmpty(titleId)) { done?.Invoke(null); return; }
            playlistId = playlistId ?? "";
            string k = Key(titleId, playlistId);
            var cached = Cached(titleId, playlistId);
            double now = Clock();
            lock (s_bundles)
            {
                if (s_fetching.Contains(k) || (s_fetchedAt.TryGetValue(k, out double at) && now - at < minSeconds)) { done?.Invoke(cached); return; }
                s_fetching.Add(k);
            }
            string etag = cached != null ? PlaylistRunPlan.Str(cached, "etag", null) : null;
            int release = -1;
            try { release = Release?.Invoke(titleId) ?? -1; } catch (Exception) { }
            var q = new List<string>();
            if (playlistId.Length > 0) q.Add("playlist=" + Uri.EscapeDataString(playlistId));
            if (release > 0) q.Add("release=" + release);
            string path = "/app/playlists/" + Uri.EscapeDataString(titleId) + "/bundle" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
            Get(path, etag, (ok, status, text, respEtag) =>
            {
                lock (s_bundles) { s_fetching.Remove(k); s_fetchedAt[k] = Clock(); }
                if (status == 409 && (text ?? "").Contains("RELEASE_TOO_OLD"))
                {
                    // The record needs newer content than this install (its goal blocks count what only newer content
                    // announces): never play it here — no cached copy either (PlaylistRunWorld.RefusalFor refuses the run).
                    JSONObject ej = null;
                    try { ej = new JSONObject(text); } catch (Exception) { }
                    int need = (int)PlaylistRunPlan.Num(ej, "requiresRelease", 0);
                    if (need <= release) need = release + 1;
                    lock (s_bundles) { s_tooOld[k] = need; s_bundles.Remove(k); }
                    try { string f = FileFor(titleId, playlistId); if (File.Exists(f)) File.Delete(f); } catch (Exception) { }
                    Log?.Invoke($"[pl-runtime] bundle {titleId}/{playlistId}: RELEASE_TOO_OLD — needs release {need}, this device has {release}: not playable here");
                    done?.Invoke(null);
                    return;
                }
                if (ok && status == 304) { done?.Invoke(cached); return; }
                if (!ok || string.IsNullOrEmpty(text)) { Log?.Invoke($"[pl-runtime] bundle {titleId}/{playlistId}: {(ok ? "empty" : "offline / " + status)} — playing the {(cached != null ? "cached" : "manifest's")} copy"); done?.Invoke(cached); return; }
                JSONObject j = null;
                try { j = new JSONObject(text); } catch (Exception) { }
                if (j == null || j.type != JSONObject.Type.Object || j.GetField("playlist") == null) { done?.Invoke(cached); return; }
                if (!string.IsNullOrEmpty(respEtag) && j.GetField("etag") == null) j.AddField("etag", respEtag);
                Put(titleId, playlistId, j);
                Log?.Invoke($"[pl-runtime] bundle {titleId}/{playlistId} rev {Rev(j)}: {j.GetField("goals")?.count ?? 0} goal blocks, missing {j.GetField("missingGoals")?.count ?? 0}");
                done?.Invoke(j);
            });
        }

        public static string Rev(JSONObject bundle)
        {
            var rv = bundle?.GetField("playlist")?.GetField("rev");
            return rv == null ? null : rv.type == JSONObject.Type.String ? rv.stringValue : rv.type == JSONObject.Type.Number ? PlaylistRunRules.Num(rv.doubleValue) : null;
        }

        /// The backend record over a plan (online wins): lua, dreamie (the bundle's script), timing, rev, each stop's goal.
        /// pinnedRev = the run's rev (rec.rev) once it started: a different rev is not applied. True when applied.
        public static bool Overlay(PlaylistRunPlan plan, JSONObject bundle, string pinnedRev = null)
        {
            if (plan == null || bundle == null) return false;
            var rec = bundle.GetField("playlist");
            var p = PlaylistRunPlanFromManifest.ParseInline(rec);
            if (p == null || (!string.IsNullOrEmpty(p.id) && p.id != plan.playlistId)) return false;
            string rev = Rev(bundle);
            if (!string.IsNullOrEmpty(pinnedRev) && rev != pinnedRev) return false;
            var dj = bundle.GetField("dreamie");
            if (dj != null && dj.type == JSONObject.Type.Object) p.dreamieJson = dj.Print();
            // The record's stop goals REPLACE the manifest's (a record "default" clears an inline compiled one).
            foreach (var leg in plan.legs)
            {
                if (leg != null) leg.dreamieJson = null;
                foreach (var s in leg?.stops ?? new List<PlaylistRunStop>()) if (s != null) { s.goal = null; s.lockSign = null; s.dreamieJson = null; }
            }
            plan.lua = null; plan.dreamieJson = null; plan.timingJson = null; plan.play = null; plan.requiresRelease = 0;
            PlaylistRunPlanFromManifest.AttachAuthored(p, plan);
            plan.rev = rev ?? plan.rev;
            return true;
        }

        public static void Clear() { lock (s_bundles) { s_bundles.Clear(); s_fetching.Clear(); s_fetchedAt.Clear(); s_tooOld.Clear(); } }
    }
}
