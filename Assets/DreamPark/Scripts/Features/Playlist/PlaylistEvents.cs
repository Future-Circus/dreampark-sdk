// ─────────────────────────────────────────────────────────────────────
//  PlaylistEvents.cs — THE event bus of a playlist run (repair-oct20 CONTRACT § 1).
//
//  Every in-game event, one shape, on every device:
//    PlaylistEvent { source, name, data (JSON object), stop (occurrenceId|null),
//                    player (playerId|null), at (session clock s), from (deviceId) }
//    source ∈ net | request | state | storage | badge | zone | enemy | gone | signal | progress | event
//
//  Fed by PlaylistAutoSignals' taps (net, request, state, storage, badge, zone,
//  enemy, gone), the title's own dp.playlist.signal / progress (signal, progress)
//  and dp.event(name, data[, stop]) (event). PlaylistRun forwards the events a
//  goal block listens to from every device to the run leader, which de-dupes
//  them by (from, at, name) and dispatches them to the stop's goal block.
//
//  census{kind=…} counts what is left of an announced kind (PlaylistAnnounce, § 4.2;
//  field='total' / 'done' for the others). DEPRECATED, kept until the titles move to
//  announcements: census{script=…|{…}, marker=…, name=…, inside=true} counts the
//  matching live objects under a stop root by script / marker / name (PlaylistCensus).
//  Pure helpers here are tested offline (Tools/playlist-run-offline).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    public sealed class PlaylistEvent
    {
        public const string Net = "net", Request = "request", State = "state", Storage = "storage", Badge = "badge",
                            Zone = "zone", Enemy = "enemy", Gone = "gone", Signal = "signal", Progress = "progress", Event = "event";
        public static readonly string[] Sources = { Net, Request, State, Storage, Badge, Zone, Enemy, Gone, Signal, Progress, Event };
        public static bool KnownSource(string s) => Array.IndexOf(Sources, s) >= 0;

        public string source, name;
        /// JSON object text ("{}" when none).
        public string dataJson = "{}";
        public string stop, player, from;
        public double at;

        JSONObject _data;
        public JSONObject Data => _data ??= SharedState.ParseFragment(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson) ?? new JSONObject();

        /// The leader's de-dupe key (CONTRACT § 1): the same event forwarded twice, or seen by two paths, counts once.
        public string DedupeKey => (from ?? "") + "|" + at.ToString("0.###", CultureInfo.InvariantCulture) + "|" + (source ?? "") + "|" + (name ?? "") + "|" + (stop ?? "");

        public string ToJson()
        {
            var sb = new StringBuilder("{\"s\":\"").Append(SharedState.JsonEscape(source ?? "")).Append("\",\"n\":\"")
                .Append(SharedState.JsonEscape(name ?? "")).Append("\",\"d\":");
            var d = Data;
            sb.Append(d != null && d.type == JSONObject.Type.Object ? d.Print() : "{}");
            if (!string.IsNullOrEmpty(stop)) sb.Append(",\"o\":\"").Append(SharedState.JsonEscape(stop)).Append('"');
            if (!string.IsNullOrEmpty(player)) sb.Append(",\"p\":\"").Append(SharedState.JsonEscape(player)).Append('"');
            if (!string.IsNullOrEmpty(from)) sb.Append(",\"f\":\"").Append(SharedState.JsonEscape(from)).Append('"');
            sb.Append(",\"a\":").Append(at.ToString("0.###", CultureInfo.InvariantCulture));
            return sb.Append('}').ToString();
        }

        /// The shape goal blocks see (e.source, e.name, e.data, e.stop, e.player, e.at, e.from).
        public string ToLuaJson()
        {
            var sb = new StringBuilder("{\"source\":\"").Append(SharedState.JsonEscape(source ?? "")).Append("\",\"name\":\"")
                .Append(SharedState.JsonEscape(name ?? "")).Append("\",\"data\":");
            var d = Data;
            sb.Append(d != null && d.type == JSONObject.Type.Object ? d.Print() : "{}");
            if (!string.IsNullOrEmpty(stop)) sb.Append(",\"stop\":\"").Append(SharedState.JsonEscape(stop)).Append('"');
            if (!string.IsNullOrEmpty(player)) sb.Append(",\"player\":\"").Append(SharedState.JsonEscape(player)).Append('"');
            if (!string.IsNullOrEmpty(from)) sb.Append(",\"from\":\"").Append(SharedState.JsonEscape(from)).Append('"');
            sb.Append(",\"at\":").Append(at.ToString("0.###", CultureInfo.InvariantCulture));
            return sb.Append('}').ToString();
        }

        public static PlaylistEvent FromJson(string json)
        {
            var j = SharedState.ParseFragment(json);
            if (j == null || j.type != JSONObject.Type.Object) return null;
            var e = new PlaylistEvent
            {
                source = PlaylistRunPlan.Str(j, "s", ""), name = PlaylistRunPlan.Str(j, "n", ""),
                stop = PlaylistRunPlan.Str(j, "o", null), player = PlaylistRunPlan.Str(j, "p", null),
                from = PlaylistRunPlan.Str(j, "f", null), at = PlaylistRunPlan.Num(j, "a"),
            };
            var d = j.GetField("d");
            e.dataJson = d != null && d.type == JSONObject.Type.Object ? d.Print() : "{}";
            return e;
        }

        public override string ToString() => $"{source}:{name}{(stop != null ? " @" + stop : "")} {dataJson}";
    }

    public static class PlaylistEvents
    {
        /// dp.event data cap (CONTRACT § 1); tap payloads above the hard cap are reduced to their scalar fields.
        public const int MaxEventDataBytes = 1024, MaxTapDataBytes = 2048;

        public static Action<string> Log = m => Debug.Log(m);
        public static Func<double> Clock = () => DreamParkSession.Now;
        public static Func<string> DeviceId = () => PlayerPresence.MyId ?? "";
        /// The local player's id (an event this device's player caused).
        public static Func<string> PlayerId = () => PlayerPresence.MyId ?? "";

        /// Every event emitted on THIS device (PlaylistRun listens: dispatch on the leader, forward elsewhere).
        public static event Action<PlaylistEvent> Raised;
        /// Events emitted on this device so far (diagnostics).
        public static int Emitted { get; private set; }

        /// Emit one event. `stop` = the occurrence id it belongs to (null = no stop / the whole run).
        public static PlaylistEvent Emit(string source, string name, string dataJson, string stop, string player = null)
        {
            if (string.IsNullOrEmpty(source) || name == null) return null;
            var e = new PlaylistEvent { source = source, name = name, dataJson = Clamp(dataJson, source == PlaylistEvent.Event ? MaxEventDataBytes : MaxTapDataBytes), stop = string.IsNullOrEmpty(stop) ? null : stop, player = player };
            try { e.from = DeviceId?.Invoke() ?? ""; } catch (Exception) { e.from = ""; }
            try { e.at = Math.Round(Clock?.Invoke() ?? 0, 3); } catch (Exception) { e.at = 0; }
            if (e.player == null) { try { e.player = PlayerId?.Invoke(); } catch (Exception) { } }
            Emitted++;
            var h = Raised;
            if (h != null)
                try { h(e); } catch (Exception ex) { Log?.Invoke("[PlaylistEvents] listener threw: " + ex.Message); }
            return e;
        }

        /// A JSON object no bigger than `max` bytes: bigger objects keep only their scalar fields (still capped); non-objects → {}.
        internal static string Clamp(string json, int max)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            var j = SharedState.ParseFragment(json);
            if (j == null) return "{}";
            if (j.type != JSONObject.Type.Object)
            {
                // A bare value rides as {"value": v} (a SharedState key's number, a payload string).
                var w = new JSONObject();
                w.AddField("value", j);
                j = w;
            }
            string s = j.Print();
            if (Encoding.UTF8.GetByteCount(s) <= max) return s;
            var small = new JSONObject();
            if (j.keys != null)
                for (int i = 0; i < j.keys.Count; i++)
                {
                    var v = j.list[i];
                    if (v == null || v.type == JSONObject.Type.Object || v.type == JSONObject.Type.Array) continue;
                    if (v.type == JSONObject.Type.String && v.stringValue != null && v.stringValue.Length > 120) continue;
                    small.AddField(j.keys[i], v);
                    if (Encoding.UTF8.GetByteCount(small.Print()) > max) { small.RemoveField(j.keys[i]); break; }
                }
            small.AddField("truncated", true);
            return small.Print();
        }

        /// A script's Game Graph id: its TextAsset name without extensions ("jubbo.lua.txt" / "jubbo.lua" → "jubbo").
        public static string ScriptId(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return "";
            string s = assetName.Trim();
            int slash = s.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (s.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.ToLowerInvariant();
        }

        /// The script ids a GameObject carries (its LuaBehaviours), Game Graph form.
        public static List<string> ScriptsOf(GameObject go)
        {
            var l = new List<string>();
            if (go == null) return l;
            foreach (var lb in go.GetComponents<LuaBehaviour>())
                if (lb != null && lb.luaScript != null) l.Add(ScriptId(lb.luaScript.name));
            return l;
        }

        /// Path of `go` under `root` ("Enemies/Jubbo (2)"), for gone events.
        public static string PathUnder(Transform root, Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }

    /// The leader's de-dupe window (CONTRACT § 1: by (from, at, name)).
    public sealed class PlaylistEventDedupe
    {
        readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> _order = new Queue<string>();
        readonly int _cap;
        public PlaylistEventDedupe(int cap = 4096) { _cap = Math.Max(16, cap); }
        /// True = first time (dispatch it); false = a duplicate.
        public bool First(PlaylistEvent e)
        {
            if (e == null) return false;
            string k = e.DedupeKey;
            if (!_seen.Add(k)) return false;
            _order.Enqueue(k);
            while (_order.Count > _cap) _seen.Remove(_order.Dequeue());
            return true;
        }
        public void Clear() { _seen.Clear(); _order.Clear(); }
    }

    /// census{kind=…[, field='total'|'done']}: an announced kind (PlaylistAnnounce, PLAYLIST-PLAN § 4.2).
    /// DEPRECATED (kept until the titles announce): census{script=…|{…}, marker=…, name=…, inside=true} (CONTRACT § 1).
    public sealed class PlaylistCensusFilter
    {
        public readonly List<string> scripts = new List<string>();
        public string marker, name;
        /// An announce kind (collect / defeat / …): counts and marks come from the announce list, not the scene walk.
        public string kind;
        /// With `kind`: 'remaining' (default) | 'total' | 'done'.
        public string field;
        public bool inside;
        /// Optional: only objects whose SharedState-less script scope reports this state (reserved; ignored when empty).
        public string state;

        public static PlaylistCensusFilter FromJson(string json)
        {
            var f = new PlaylistCensusFilter();
            var j = SharedState.ParseFragment(json);
            if (j == null || j.type != JSONObject.Type.Object) return f;
            var s = j.GetField("script") ?? j.GetField("scripts");
            if (s != null)
            {
                if (s.type == JSONObject.Type.String) f.scripts.Add(PlaylistEvents.ScriptId(s.stringValue));
                else if (s.type == JSONObject.Type.Array && s.list != null)
                    foreach (var x in s.list) if (x != null && x.type == JSONObject.Type.String) f.scripts.Add(PlaylistEvents.ScriptId(x.stringValue));
            }
            f.marker = PlaylistRunPlan.Str(j, "marker", null)?.ToLowerInvariant();
            f.name = PlaylistRunPlan.Str(j, "name", null);
            f.state = PlaylistRunPlan.Str(j, "state", null);
            f.inside = PlaylistRunPlan.Bool(j, "inside", false);
            f.kind = AnnounceKinds.Normalize(PlaylistRunPlan.Str(j, "kind", null));
            f.field = PlaylistRunPlan.Str(j, "field", null);
            return f;
        }

        public bool Empty => kind == null && scripts.Count == 0 && string.IsNullOrEmpty(marker) && string.IsNullOrEmpty(name);

        /// Does an object with these script ids / marker kind / name match?
        public bool Matches(IList<string> objScripts, string markerKind, string objName)
        {
            if (Empty) return false;
            if (scripts.Count > 0)
            {
                bool any = false;
                foreach (var s in objScripts ?? Array.Empty<string>()) if (scripts.Contains(s)) { any = true; break; }
                if (!any) return false;
            }
            if (!string.IsNullOrEmpty(marker) && !string.Equals(marker, markerKind, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(name) && !PlaylistAutoRules.Glob(name, objName ?? "")) return false;
            return true;
        }

        /// `inside`: within the stop's footprint (local XZ half-size + margin) and above the floor − 1 m.
        public static bool InsideFootprint(Vector3 local, Vector2 footprint, float margin = 0.25f, float below = 1f) =>
            Mathf.Abs(local.x) <= footprint.x * 0.5f + margin && Mathf.Abs(local.z) <= footprint.y * 0.5f + margin && local.y >= -below;
    }

    public static class PlaylistCensus
    {
        /// The live objects under `root` that match: active in the hierarchy, and (inside) still in the footprint.
        public static List<GameObject> Find(GameObject root, PlaylistCensusFilter f)
        {
            var list = new List<GameObject>();
            if (root == null || f == null || f.Empty) return list;
            if (f.kind != null) { foreach (var t in PlaylistAnnounce.Live(root, f.kind)) list.Add(t.gameObject); return list; }
            var tpl = root.GetComponentInChildren<AttractionTemplate>(true);
            Transform frame = tpl != null ? tpl.transform : root.transform;
            Vector2 fp = tpl != null ? tpl.RuntimeFootprintMeters : Vector2.zero;
            var seen = new HashSet<GameObject>();
            void Consider(GameObject go)
            {
                if (go == null || !go.activeInHierarchy || !seen.Add(go)) return;
                string mk = null;
                if (!string.IsNullOrEmpty(f.marker))
                {
                    var m = go.GetComponent<PlaylistObjectiveMarker>();
                    mk = m != null ? m.kind.ToString().ToLowerInvariant() : null;
                }
                if (!f.Matches(PlaylistEvents.ScriptsOf(go), mk, go.name)) return;
                if (f.inside && fp.x > 0 && fp.y > 0)
                {
                    Vector3 local = frame.InverseTransformPoint(go.transform.position);
                    // Footprint is in meters; the frame may be scaled (frames never scale content, but be safe).
                    Vector3 ls = frame.lossyScale;
                    local = new Vector3(local.x * ls.x, local.y * ls.y, local.z * ls.z);
                    if (!PlaylistCensusFilter.InsideFootprint(local, fp)) return;
                }
                list.Add(go);
            }
            if (f.scripts.Count > 0)
                foreach (var lb in root.GetComponentsInChildren<LuaBehaviour>(false)) Consider(lb != null ? lb.gameObject : null);
            else if (!string.IsNullOrEmpty(f.marker))
                foreach (var m in root.GetComponentsInChildren<PlaylistObjectiveMarker>(false)) Consider(m != null ? m.gameObject : null);
            else
                foreach (var t in root.GetComponentsInChildren<Transform>(false)) if (t != root.transform) Consider(t.gameObject);
            return list;
        }

        public static int Count(GameObject root, PlaylistCensusFilter f)
        {
            if (f != null && f.kind != null && root != null)
            {
                var c = PlaylistAnnounce.Count(root, f.kind);
                return f.field == "total" ? c.total : f.field == "done" ? c.done : c.remaining;
            }
            return Find(root, f).Count;
        }
    }
}
