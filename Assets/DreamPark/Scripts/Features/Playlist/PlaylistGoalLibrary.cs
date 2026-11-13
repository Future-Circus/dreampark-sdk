// ─────────────────────────────────────────────────────────────────────
//  PlaylistGoalLibrary.cs — where a stop's GOAL BLOCK comes from
//  (repair-oct20 CONTRACT § 2). Precedence per STOP, never per title:
//    1. the stop's inline block (`goal: {lua}`)
//    2. the title's default block for the stop's kind, keyed "<attractionId>.<kind>":
//       the backend copy (playlist bundle, online) wins over the manifest copy
//       (compiled from Assets/Content/<Title>/.dreampark-goals/, offline)
//    3. the title's own dp.playlist calls (no block: the runner applies them)
//    4. PlaylistAutoRules.Generic (the signal adapter)
//  Thread-safe: manifests register from ISerializationCallbackReceiver.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;

namespace DreamPark
{
    /// The registry is engine- and rule-free (JSONObject only: the SDK's Studio harness compiles it alone); the stop
    /// resolution half lives beside PlaylistRun (PlaylistRun.cs).
    public static partial class PlaylistGoalLibrary
    {
        public const string SourceInline = "inline", SourceBackend = "backend", SourceManifest = "manifest";

        static readonly object s_lock = new object();
        static readonly Dictionary<string, Dictionary<string, string>> s_manifest = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Dictionary<string, string>> s_backend = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// A Beta upload and its base title share one library.
        public static string BaseTitle(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return "";
            return contentId.EndsWith("Beta", StringComparison.OrdinalIgnoreCase) ? contentId.Substring(0, contentId.Length - 4) : contentId;
        }

        static Dictionary<string, string> Parse(string goalsJson)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JSONObject j = null;
            try { j = new JSONObject(goalsJson ?? ""); } catch (Exception) { }
            if (j == null || j.type != JSONObject.Type.Object || j.keys == null) return d;
            for (int i = 0; i < j.keys.Count; i++)
            {
                var v = j.list[i];
                string lua = v == null ? null
                    : v.type == JSONObject.Type.String ? v.stringValue
                    : v.type == JSONObject.Type.Object && v.GetField("lua") is JSONObject l && l.type == JSONObject.Type.String ? l.stringValue : null;   // the backend's {lua, rev, …}
                if (!string.IsNullOrWhiteSpace(lua)) d[j.keys[i]] = lua;
            }
            return d;
        }

        public static void RegisterManifest(string contentId, string goalsJson)
        {
            var d = Parse(goalsJson);
            lock (s_lock) s_manifest[BaseTitle(contentId)] = d;
        }

        /// The playlist bundle's goal blocks (online; they win over the manifest copy). goalsJson = {key: lua | {lua, rev}}.
        public static void RegisterBackend(string contentId, string goalsJson)
        {
            var d = Parse(goalsJson);
            lock (s_lock)
            {
                string t = BaseTitle(contentId);
                if (!s_backend.TryGetValue(t, out var cur)) s_backend[t] = cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in d) cur[kv.Key] = kv.Value;
            }
        }

        /// Every block known for a title (offline cache).
        public static Dictionary<string, string> All(string contentId)
        {
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (s_lock)
            {
                string t = BaseTitle(contentId);
                if (s_manifest.TryGetValue(t, out var m)) foreach (var kv in m) all[kv.Key] = kv.Value;
                if (s_backend.TryGetValue(t, out var b)) foreach (var kv in b) all[kv.Key] = kv.Value;
            }
            return all;
        }

        /// Every block known for a title RIGHT NOW with its source (backend over manifest): what a run freezes at its first
        /// use when it has no bundle of its own (record v2 § 8.9: goal revs are pinned per run).
        public static Dictionary<string, (string lua, string source)> Snapshot(string contentId)
        {
            var all = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            lock (s_lock)
            {
                string t = BaseTitle(contentId);
                if (s_manifest.TryGetValue(t, out var m)) foreach (var kv in m) all[kv.Key] = (kv.Value, SourceManifest);
                if (s_backend.TryGetValue(t, out var b)) foreach (var kv in b) all[kv.Key] = (kv.Value, SourceBackend);
            }
            return all;
        }

        /// The keys a stop's default block may have, best first: "<attraction leaf>.<kind>", then "<attractionId>.<kind>".
        public static List<string> Keys(string attraction, string attractionId, string kind)
        {
            var keys = new List<string>();
            string leaf = Leaf(attraction);
            if (!string.IsNullOrEmpty(leaf)) keys.Add(leaf + "." + kind);
            if (!string.IsNullOrEmpty(attractionId) && attractionId != leaf) keys.Add(attractionId + "." + kind);
            return keys;
        }

        public static void Clear() { lock (s_lock) { s_manifest.Clear(); s_backend.Clear(); } }

        static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        /// The title's default block for (attraction, kind), or null. Keys try the attraction leaf ("A_LavaPits"), then its id.
        public static string Find(string contentId, string attraction, string attractionId, string kind, out string key, out string source)
        {
            key = null; source = null;
            if (string.IsNullOrEmpty(kind)) return null;
            string t = BaseTitle(contentId);
            var names = new List<string>();
            string leaf = Leaf(attraction);
            if (!string.IsNullOrEmpty(leaf)) names.Add(leaf);
            if (!string.IsNullOrEmpty(attractionId) && !names.Contains(attractionId)) names.Add(attractionId);
            lock (s_lock)
            {
                foreach (var n in names)
                {
                    string k = n + "." + kind;
                    if (s_backend.TryGetValue(t, out var b) && b.TryGetValue(k, out var lb)) { key = k; source = SourceBackend; return lb; }
                    if (s_manifest.TryGetValue(t, out var m) && m.TryGetValue(k, out var lm)) { key = k; source = SourceManifest; return lm; }
                }
            }
            return null;
        }

    }
}
