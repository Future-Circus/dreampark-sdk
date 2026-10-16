#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using DreamPark.API;
using DreamPark.ParkBuilder;
using UnityEngine;
using UnityEngine.Rendering;
using XLua;

namespace DreamPark.Playtest
{
    /// <summary>
    /// The playtester's senses. Two halves:
    ///
    /// 1. An event stream — console errors, Lua exceptions and prints, zone
    ///    changes, storage writes and every contact the avatar's probes make —
    ///    read incrementally with a cursor, so each action reports only what it
    ///    caused.
    /// 2. On-demand snapshots — a text description of the world around the player
    ///    (interactable objects with short ids, their live Lua state, attractions,
    ///    world-space text) plus head-camera and overhead screenshots.
    ///
    /// Everything here is read-only against content except <see cref="LuaEval"/>,
    /// which exists so a tester can check state the way a developer would in a
    /// console. Editor-only.
    /// </summary>
    public static class PlaytestObserver
    {
        public class Event
        {
            public float time;
            public string kind;
            public string text;
            public int count = 1;
        }

        static readonly object _lock = new object();
        static readonly List<Event> _events = new List<Event>();
        static bool _listening;

        // Stable short handles ("e12") for objects the agent has been shown.
        static readonly Dictionary<int, string> _idsByInstance = new Dictionary<int, string>();
        static readonly Dictionary<string, GameObject> _objectsById = new Dictionary<string, GameObject>();
        static int _nextId = 1;

        static readonly FieldInfo ScopeField =
            typeof(LuaBehaviour).GetField("scriptScopeTable", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly HashSet<string> HiddenScopeKeys = new HashSet<string>
        {
            "self", "dp", "peer_id", "storage", "net_send", "net_send_typed", "onmessage", "_ENV",
            "awake", "start", "update", "ondestroy", "onenable", "ondisable", "onnet", "fixedupdate",
            "lateupdate", "ontriggerenter", "ontriggerexit", "ontriggerstay", "oncollisionenter",
            "oncollisionexit", "oncollisionstay", "onapplicationpause", "onapplicationfocus",
            "onzoneenter", "onzoneexit", "onready",
        };

        public static int EventCount { get { lock (_lock) return _events.Count; } }

        // ── Event stream ──────────────────────────────────────────────────

        public static void Begin()
        {
            lock (_lock) _events.Clear();
            _idsByInstance.Clear();
            _objectsById.Clear();
            _nextId = 1;
            if (_listening) return;
            _listening = true;
            Application.logMessageReceivedThreaded += OnLog;
            GameArea.OnContentZoneChanged += OnZone;
            GameStorageAPI.OnNumericOp += OnNumericOp;
            GameStorageAPI.OnSetOp += OnSetOp;
            LevelObjectManager.OnObjectsEnabledChanged += OnObjectsEnabled;
        }

        public static void End()
        {
            if (!_listening) return;
            _listening = false;
            Application.logMessageReceivedThreaded -= OnLog;
            GameArea.OnContentZoneChanged -= OnZone;
            GameStorageAPI.OnNumericOp -= OnNumericOp;
            GameStorageAPI.OnSetOp -= OnSetOp;
            LevelObjectManager.OnObjectsEnabledChanged -= OnObjectsEnabled;
        }

        static void Add(string kind, string text)
        {
            float now;
            try { now = Time.realtimeSinceStartup; } catch { now = 0f; }
            lock (_lock)
            {
                // Collapse repeats (an error thrown from update() fires every frame).
                for (int i = _events.Count - 1; i >= 0 && i >= _events.Count - 40; i--)
                {
                    var e = _events[i];
                    if (e.kind == kind && e.text == text) { e.count++; e.time = now; return; }
                }
                _events.Add(new Event { time = now, kind = kind, text = text });
                if (_events.Count > 4000) _events.RemoveRange(0, 1000);
            }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(message) || message.StartsWith("[Playtest]")) return;
            bool lua = message.Contains("LuaException") || message.Contains("[string \"") || message.Contains(".lua\"]");
            switch (type)
            {
                case LogType.Exception:
                case LogType.Error:
                case LogType.Assert:
                {
                    string first = FirstLines(message, 4);
                    if (type == LogType.Exception && !lua) first += " @ " + FirstLines(stack, 2);
                    Add(lua ? "lua error" : "error", first);
                    break;
                }
                case LogType.Warning:
                    Add("warning", FirstLines(message, 2));
                    break;
                default:
                    if (message.StartsWith("LUA: ")) Add("lua print", FirstLines(message.Substring(5), 3));
                    break;
            }
        }

        static void OnZone(GameArea prev, GameArea now)
        {
            string p = prev != null ? prev.gameObject.name : "none";
            string n = now != null ? now.gameObject.name : "none";
            Add("zone", $"attraction zone changed: {p} -> {n}");
        }

        static void OnNumericOp(string contentId, string slug, string key, string op, double amount, double value)
        {
            string scope = string.IsNullOrEmpty(slug) ? "game" : slug;
            Add("storage", $"storage[{scope}].{key} {op} {Num(amount)} -> {Num(value)}");
        }

        static void OnSetOp(string contentId, string slug, string key, object value)
        {
            string scope = string.IsNullOrEmpty(slug) ? "game" : slug;
            Add("storage", $"storage[{scope}].{key} = {value}");
        }

        static void OnObjectsEnabled(bool enabled)
        {
            Add("world", enabled ? "park content went live (objects enabled)" : "park content parked (loading)");
        }

        public static void RecordContact(PlaytestProbe probe, Collider other, string verb)
        {
            string id = IdFor(other.attachedRigidbody != null ? other.attachedRigidbody.gameObject : other.gameObject);
            Add("contact", $"{probe.Label} {verb} {id} \"{other.gameObject.name}\"{ScriptSuffix(other.gameObject)}");
        }

        public static void Note(string kind, string text) => Add(kind, text);

        /// Events since <paramref name="cursor"/>; advances it.
        public static List<Event> Drain(ref int cursor)
        {
            lock (_lock)
            {
                if (cursor > _events.Count) cursor = 0;
                var slice = _events.Skip(cursor).Select(e => new Event { time = e.time, kind = e.kind, text = e.text, count = e.count }).ToList();
                cursor = _events.Count;
                return slice;
            }
        }

        public static List<Event> All()
        {
            lock (_lock) return _events.Select(e => new Event { time = e.time, kind = e.kind, text = e.text, count = e.count }).ToList();
        }

        public static string FormatEvents(List<Event> events, int max = 30)
        {
            if (events == null || events.Count == 0) return "(nothing new)";
            var sb = new StringBuilder();
            // Errors first: they are what a tester most needs to see and what a long
            // list of prints would otherwise push out.
            var ordered = events.Where(e => e.kind.Contains("error")).Concat(events.Where(e => !e.kind.Contains("error"))).ToList();
            foreach (var e in ordered.Take(max))
            {
                sb.Append("- [").Append(e.kind).Append("] ").Append(e.text);
                if (e.count > 1) sb.Append(" (x").Append(e.count).Append(')');
                sb.Append('\n');
            }
            if (ordered.Count > max) sb.Append($"- ... {ordered.Count - max} more\n");
            return sb.ToString().TrimEnd();
        }

        // ── Object handles ────────────────────────────────────────────────

        public static string IdFor(GameObject go)
        {
            if (go == null) return "?";
            int key = go.GetInstanceID();
            if (!_idsByInstance.TryGetValue(key, out var id))
            {
                id = "e" + _nextId++;
                _idsByInstance[key] = id;
            }
            _objectsById[id] = go;
            return id;
        }

        /// Resolves an id from a snapshot, or falls back to the nearest active object
        /// with that exact name (then a case-insensitive substring), so an external
        /// agent can also say "Start Button".
        public static GameObject Resolve(string idOrName, Vector3 near)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            idOrName = idOrName.Trim();
            if (_objectsById.TryGetValue(idOrName, out var go) && go != null) return go;

            GameObject best = null;
            float bestScore = float.MaxValue;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (!t.gameObject.activeInHierarchy) continue;
                string n = t.gameObject.name;
                float penalty;
                if (n == idOrName) penalty = 0f;
                else if (n.IndexOf(idOrName, StringComparison.OrdinalIgnoreCase) >= 0) penalty = 1000f;
                else continue;
                float score = penalty + (t.position - near).sqrMagnitude;
                if (score < bestScore) { bestScore = score; best = t.gameObject; }
            }
            if (best != null) IdFor(best);
            return best;
        }

        // ── Geometry helpers ──────────────────────────────────────────────

        public static Bounds BoundsOf(GameObject go)
        {
            bool has = false;
            var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
            if (!has)
            {
                foreach (var c in go.GetComponentsInChildren<Collider>())
                {
                    if (!c.enabled) continue;
                    if (!has) { b = c.bounds; has = true; } else b.Encapsulate(c.bounds);
                }
            }
            return b;
        }

        /// The point a hand should aim for: the closest point on the object's own
        /// colliders (what content actually tests against), else its visual centre.
        public static Vector3 TouchPoint(GameObject go, Vector3 from)
        {
            Vector3 best = BoundsOf(go).center;
            float bestD = float.MaxValue;
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || PlaytestAvatar.IsPlayerCollider(c)) continue;
                Vector3 p;
                bool convexOk = !(c is MeshCollider mc) || mc.convex;
                p = convexOk ? c.ClosestPoint(from) : c.bounds.ClosestPoint(from);
                // ClosestPoint returns `from` itself when `from` is inside.
                if ((p - from).sqrMagnitude < 1e-6f) p = c.bounds.center;
                float d = (p - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        static string Bearing(Vector3 head, Vector3 forward, Vector3 point)
        {
            Vector3 to = point - head;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.05f) return "0.0 m, here";
            float angle = Vector3.SignedAngle(new Vector3(forward.x, 0f, forward.z), to, Vector3.up);
            string dir = Mathf.Abs(angle) < 10f ? "ahead"
                : Mathf.Abs(angle) > 160f ? "behind"
                : $"{Mathf.Abs(angle):0}° {(angle > 0 ? "right" : "left")}";
            return $"{dist:0.0} m, {dir}";
        }

        static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"({v.x.ToString("0.00", CultureInfo.InvariantCulture)}, {v.y.ToString("0.00", CultureInfo.InvariantCulture)}, {v.z.ToString("0.00", CultureInfo.InvariantCulture)})";

        static string FirstLines(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(n);
            string joined = string.Join(" | ", lines);
            return joined.Length > 600 ? joined.Substring(0, 600) + "…" : joined;
        }

        // ── Lua introspection ─────────────────────────────────────────────

        static LuaTable ScopeOf(LuaBehaviour lb) => ScopeField?.GetValue(lb) as LuaTable;

        static string ScriptName(LuaBehaviour lb) => lb.luaScript != null ? lb.luaScript.name : "(no script)";

        static string ScriptSuffix(GameObject go)
        {
            var lbs = go.GetComponents<LuaBehaviour>();
            return lbs.Length == 0 ? "" : " [" + string.Join(", ", lbs.Select(ScriptName)) + "]";
        }

        /// Scope-level values (numbers/strings/bools) and callable function names.
        /// Script `local`s are invisible by design of Lua; globals are what other
        /// scripts can see too, so they are also what a tester can reason about.
        static void ReadScope(LuaTable scope, int maxValues, out List<string> values, out List<string> functions)
        {
            values = new List<string>();
            functions = new List<string>();
            if (scope == null) return;
            var v = values;
            var f = functions;
            try
            {
                scope.ForEach<object, object>((k, val) =>
                {
                    if (!(k is string key) || HiddenScopeKeys.Contains(key)) return;
                    switch (val)
                    {
                        case LuaFunction _: f.Add(key); break;
                        case bool b: v.Add($"{key}={(b ? "true" : "false")}"); break;
                        case string s: v.Add($"{key}=\"{(s.Length > 40 ? s.Substring(0, 40) + "…" : s)}\""); break;
                        case double d: v.Add($"{key}={Num(d)}"); break;
                        case long l: v.Add($"{key}={l}"); break;
                        case int i: v.Add($"{key}={i}"); break;
                        case float fl: v.Add($"{key}={Num(fl)}"); break;
                        case LuaTable t: v.Add(t.Length > 0 ? $"{key}={{list of {t.Length}}}" : $"{key}={{table}}"); break;
                    }
                });
            }
            catch (Exception) { /* a scope mid-teardown can throw; skip it */ }
            values.Sort(StringComparer.Ordinal);
            if (values.Count > maxValues) values = values.Take(maxValues).Append("…").ToList();
            functions.Sort(StringComparer.Ordinal);
        }

        public static string FormatLuaValue(object o, int depth = 0)
        {
            switch (o)
            {
                case null: return "nil";
                case string s: return "\"" + s + "\"";
                case bool b: return b ? "true" : "false";
                case double d: return Num(d);
                case float f: return Num(f);
                case LuaFunction _: return "function";
                case LuaTable t:
                {
                    if (depth > 1) return "{…}";
                    var parts = new List<string>();
                    int n = 0;
                    t.ForEach<object, object>((k, v) =>
                    {
                        if (n++ < 20) parts.Add($"{k}={FormatLuaValue(v, depth + 1)}");
                    });
                    if (n > 20) parts.Add("…");
                    return "{" + string.Join(", ", parts) + "}";
                }
                case UnityEngine.Object uo: return uo == null ? "nil (destroyed)" : $"{uo.GetType().Name} \"{uo.name}\"";
                default: return o.ToString();
            }
        }

        /// Evaluates a Lua expression (or statement) inside an object's script
        /// scope, so `get_points()` resolves the way it would for that script.
        public static string LuaEval(GameObject go, string code, string scriptName)
        {
            var lbs = go.GetComponents<LuaBehaviour>();
            if (lbs.Length == 0) lbs = go.GetComponentsInChildren<LuaBehaviour>();
            if (lbs.Length == 0) return "error: no LuaBehaviour on that object.";
            var lb = string.IsNullOrEmpty(scriptName)
                ? lbs[0]
                : lbs.FirstOrDefault(l => ScriptName(l).IndexOf(scriptName, StringComparison.OrdinalIgnoreCase) >= 0) ?? lbs[0];
            var scope = ScopeOf(lb);
            if (scope == null) return $"error: {ScriptName(lb)} has not booted yet (object parked or never enabled).";

            var env = LuaBehaviour.GetLuaEnv();
            object[] results;
            try
            {
                results = env.DoString("return " + code, "playtest_eval", scope);
            }
            catch (LuaException)
            {
                try { results = env.DoString(code, "playtest_eval", scope); }
                catch (Exception e) { return "error: " + e.Message; }
            }
            if (results == null || results.Length == 0) return $"ok ({ScriptName(lb)}): no value";
            return $"ok ({ScriptName(lb)}): " + string.Join(", ", results.Select(r => FormatLuaValue(r)));
        }

        // ── Snapshot ──────────────────────────────────────────────────────

        public static string Snapshot(PlaytestAvatar avatar, float radius = 25f, int maxEntities = 30)
        {
            var sb = new StringBuilder();
            var cam = Camera.main;
            Vector3 head = avatar != null ? avatar.HeadPosition : cam != null ? cam.transform.position : Vector3.zero;
            Vector3 fwd = avatar != null ? avatar.Forward : cam != null ? cam.transform.forward : Vector3.forward;
            float floor = avatar != null ? avatar.FloorY : head.y - 1.6f;
            Plane[] frustum = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;

            // Player
            var zone = GameArea.currentGameArea;
            sb.Append("PLAYER: head ").Append(V(head));
            if (avatar != null)
            {
                sb.Append($", eye height {avatar.EyeHeight:0.00} m, facing yaw {Mathf.Repeat(avatar.Yaw, 360f):0}°, pitch {avatar.Pitch:0}°");
                if (!avatar.FloorKnown) sb.Append(", NO FLOOR UNDER YOU");
            }
            sb.Append(", in attraction: ").Append(zone != null ? $"{zone.gameObject.name}{(zone.isPlaying ? "" : " (not playing)")}" : "none");
            if (!LevelObjectManager.objectsEnabled) sb.Append(" — park content is still loading/parked");
            sb.Append('\n');
            if (avatar != null)
            {
                sb.Append("HANDS: ");
                foreach (var h in new[] { PlaytestHand.Left, PlaytestHand.Right })
                    sb.Append(h).Append(avatar.IsHolding(h) ? " at " : " ").Append(V(avatar.HandPosition(h))).Append("; ");
                sb.Append('\n');
            }

            // Attractions
            var areas = GameArea.allGameAreas.Where(a => a != null && a.isActiveAndEnabled)
                .OrderBy(a => a.SignedDistanceTo(head)).Take(12).ToList();
            if (areas.Count > 0)
            {
                sb.Append("ATTRACTIONS:\n");
                foreach (var a in areas)
                {
                    float d = a.SignedDistanceTo(head);
                    string where = d <= 0 ? "you are inside" : $"edge {d:0.0} m away, centre {Bearing(head, fwd, a.transform.position)}";
                    sb.Append($"- {IdFor(a.gameObject)} \"{a.gameObject.name}\" ({where})\n");
                }
            }

            // Interactables
            var rigScripts = new List<LuaBehaviour>();
            var candidates = new HashSet<GameObject>();
            foreach (var lb in UnityEngine.Object.FindObjectsByType<LuaBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (lb.GetComponentInParent<PlayerRig>(true) != null) rigScripts.Add(lb);
                else candidates.Add(lb.gameObject);
            }
            foreach (var c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!c.enabled || PlaytestAvatar.IsPlayerCollider(c)) continue;
                // Triggers, tagged colliders (content often filters contacts by tag —
                // the Sample's coins are plain solid spheres tagged "Coin") and anything
                // inside a prop, which is an interactive element by definition.
                bool interesting = c.isTrigger || !c.CompareTag("Untagged") || c.GetComponentInParent<PropTemplate>() != null;
                if (interesting) candidates.Add(c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject);
            }
            foreach (var rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!rb.isKinematic && !PlaytestAvatar.IsPlayerCollider(rb.GetComponent<Collider>())) candidates.Add(rb.gameObject);
            }

            var near = candidates
                .Where(go => go != null && go.GetComponentInParent<PlaytestProbe>() == null)
                .Select(go => (go, b: BoundsOf(go)))
                // Generated floors are tagged, solid and everywhere; they aren't things to interact with.
                .Where(x => !(x.b.size.y < 0.2f && Mathf.Max(x.b.size.x, x.b.size.z) > 2f && x.go.GetComponent<LuaBehaviour>() == null))
                .Select(x => (x.go, x.b, d: Vector3.Distance(head, x.b.center)))
                .Where(x => x.d <= radius || x.go.GetComponent<LuaBehaviour>() != null && x.go.name.Contains("Game Manager"))
                .OrderBy(x => x.d)
                .ToList();

            sb.Append($"OBJECTS within {radius:0} m (nearest first; use the ids in actions):\n");
            if (near.Count == 0) sb.Append("- none. Walk toward an attraction or take an overhead screenshot.\n");
            foreach (var (go, b, _) in near.Take(maxEntities))
            {
                sb.Append("- ").Append(IdFor(go)).Append(" \"").Append(go.name).Append('"');
                var tags = new List<string>();
                var lbs = go.GetComponents<LuaBehaviour>();
                if (lbs.Length > 0) tags.Add("lua: " + string.Join(", ", lbs.Select(ScriptName)));
                if (!go.CompareTag("Untagged")) tags.Add("tag " + go.tag);
                var cols = go.GetComponentsInChildren<Collider>().Where(c => c.enabled).ToList();
                if (cols.Any(c => c.isTrigger)) tags.Add("trigger");
                else if (cols.Count > 0 && lbs.Length == 0) tags.Add("solid");
                var body = go.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic) tags.Add("physics");
                if (tags.Count > 0) sb.Append(" [").Append(string.Join("; ", tags)).Append(']');
                sb.Append(' ').Append(Bearing(head, fwd, b.center));
                sb.Append($", {b.center.y - floor:0.00} m above floor, size {b.size.x:0.00}×{b.size.y:0.00}×{b.size.z:0.00}");
                if (frustum != null && GeometryUtility.TestPlanesAABB(frustum, b)) sb.Append(", in view");
                var area = go.GetComponentInParent<GameArea>();
                if (area != null && area.gameObject != go) sb.Append(", in ").Append(area.gameObject.name);
                foreach (var lb in lbs)
                {
                    ReadScope(ScopeOf(lb), 8, out var values, out var functions);
                    if (values.Count > 0) sb.Append(" | state: ").Append(string.Join(" ", values));
                    if (functions.Count > 0) sb.Append(" | fns: ").Append(string.Join(", ", functions.Take(10)));
                }
                sb.Append('\n');
            }
            if (near.Count > maxEntities) sb.Append($"- … {near.Count - maxEntities} more further away\n");

            if (rigScripts.Count > 0)
            {
                sb.Append("PLAYER SYSTEMS (scripts on your own rig):\n");
                foreach (var lb in rigScripts.Take(12))
                {
                    ReadScope(ScopeOf(lb), 8, out var values, out var functions);
                    sb.Append($"- {IdFor(lb.gameObject)} \"{lb.gameObject.name}\" [lua: {ScriptName(lb)}]{HandSuffix(lb.gameObject)}");
                    if (values.Count > 0) sb.Append(" | state: ").Append(string.Join(" ", values));
                    if (functions.Count > 0) sb.Append(" | fns: ").Append(string.Join(", ", functions.Take(10)));
                    sb.Append('\n');
                }
            }

            // World-space text: scoreboards, instructions, HUDs.
            var texts = UnityEngine.Object.FindObjectsByType<TMPro.TMP_Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(t => t.enabled && !string.IsNullOrWhiteSpace(t.text))
                .Select(t => (t, d: Vector3.Distance(head, t.transform.position)))
                .Where(x => x.d < 15f)
                .OrderBy(x => x.d)
                .Take(10)
                .ToList();
            if (texts.Count > 0)
            {
                sb.Append("TEXT IN THE WORLD:\n");
                foreach (var (t, _) in texts)
                {
                    string s = t.text.Replace("\n", " / ");
                    if (s.Length > 80) s = s.Substring(0, 80) + "…";
                    sb.Append($"- \"{s}\" on {IdFor(t.gameObject)} \"{t.gameObject.name}\" ({Bearing(head, fwd, t.transform.position)})\n");
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static string Inspect(GameObject go, PlaytestAvatar avatar)
        {
            var sb = new StringBuilder();
            Vector3 head = avatar != null ? avatar.HeadPosition : go.transform.position;
            Vector3 fwd = avatar != null ? avatar.Forward : Vector3.forward;
            var b = BoundsOf(go);
            sb.Append($"{IdFor(go)} \"{go.name}\" active={go.activeInHierarchy} at {V(go.transform.position)} ({Bearing(head, fwd, b.center)}), bounds size {V(b.size)}\n");
            sb.Append("path: ").Append(PathOf(go.transform)).Append('\n');
            sb.Append("components: ").Append(string.Join(", ", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name))).Append('\n');
            foreach (var c in go.GetComponentsInChildren<Collider>())
                sb.Append($"collider: {c.GetType().Name} on \"{c.gameObject.name}\" trigger={c.isTrigger} enabled={c.enabled} rigidbody={(c.attachedRigidbody != null ? (c.attachedRigidbody.isKinematic ? "kinematic" : "dynamic") : "none")}\n");
            foreach (var lb in go.GetComponents<LuaBehaviour>())
            {
                ReadScope(ScopeOf(lb), 40, out var values, out var functions);
                sb.Append($"lua {ScriptName(lb)} booted={ScopeOf(lb) != null}\n");
                if (values.Count > 0) sb.Append("  state: ").Append(string.Join(" ", values)).Append('\n');
                if (functions.Count > 0) sb.Append("  functions: ").Append(string.Join(", ", functions)).Append('\n');
            }
            var kids = new List<string>();
            foreach (Transform child in go.transform)
                kids.Add($"{IdFor(child.gameObject)} \"{child.name}\"{(child.gameObject.activeSelf ? "" : " (inactive)")}{ScriptSuffix(child.gameObject)}");
            if (kids.Count > 0) sb.Append("children: ").Append(string.Join("; ", kids.Take(30))).Append(kids.Count > 30 ? "; …" : "").Append('\n');
            return sb.ToString().TrimEnd();
        }

        /// Which of the bot's hands a rig object rides on. Content usually counts a
        /// touch only through its own rig colliders, and a Both-hands tracker picks
        /// the LEFT hand in the Editor when nothing is tracked — without this the
        /// tester would reach with the wrong hand and conclude pickups are broken.
        static string HandSuffix(GameObject go)
        {
            var tracker = go.GetComponentInParent<HandTracker>();
            if (tracker == null) return go.GetComponentInParent<BodyTracker>() != null ? " (rides your body)" : "";
            if (tracker.isUsingLeftHand) return " (rides your LEFT hand)";
            if (tracker.isUsingRightHand) return " (rides your RIGHT hand)";
            return " (on a hand tracker)";
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null && parts.Count < 8; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ── Screenshots ───────────────────────────────────────────────────

        public static byte[] CaptureHead(int width = 768, int height = 432)
        {
            var cam = Camera.main;
            return cam != null ? Render(cam, width, height) : null;
        }

        /// A camera straight above the player, rotated so the player's facing is
        /// image-up and the player is at the centre. The cheapest way to give a
        /// model a map of an attraction's layout.
        public static byte[] CaptureOverhead(PlaytestAvatar avatar, float altitude = 10f, int width = 768, int height = 768)
        {
            var head = Camera.main;
            if (avatar == null) return null;
            var go = new GameObject("[Playtest Overhead Camera]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var cam = go.AddComponent<Camera>();
                if (head != null) cam.cullingMask = head.cullingMask;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = altitude + 20f;
                go.transform.SetPositionAndRotation(
                    new Vector3(avatar.HeadPosition.x, avatar.FloorY + altitude, avatar.HeadPosition.z),
                    Quaternion.Euler(90f, avatar.Yaw, 0f));
                return Render(cam, width, height);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static byte[] Render(Camera cam, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            var prevTarget = cam.targetTexture;
            Texture2D tex = null;
            try
            {
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request))
                {
                    RenderPipeline.SubmitRenderRequest(cam, request);
                }
                else
                {
                    cam.targetTexture = rt;
                    cam.Render();
                }
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                return tex.EncodeToJPG(70);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Playtest] Screenshot failed: " + e.Message);
                return null;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
