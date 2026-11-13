// ─────────────────────────────────────────────────────────────────────
//  PlaylistAnnounceRuntime.cs — the Unity side of announcements (PLAYLIST-PLAN § 4.2; pl-announce, Oct 21 2026).
//    • PlaylistAnnounce: dp.announce / dp.announce_done / the Announce component land here. One live list per
//      attraction (PlaylistAnnounceList), shared on the attraction's bus as SharedState "dp.announce".
//    • An object's stable key = its path under the attraction root (clone-stripped names, "~k" for the k-th sibling
//      of the same name — sal-net's rel_key), so it is the same on every headset and after a reset.
//    • PlaylistAnnounceTag on every announced object: switched off or destroyed while its attraction is still up =
//      done, automatically (checked a frame later, so an attraction being packed away or unloaded never "completes"
//      everything inside it).
//    • Goal blocks read it through the stop root: Count(root, kind) (stop.count), Live(root, kind) (stop.mark).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DreamPark
{
    public static class PlaylistAnnounce
    {
        public const string StateName = "dp.announce";

        static readonly Dictionary<int, PlaylistAnnounceList> s_lists = new Dictionary<int, PlaylistAnnounceList>();
        /// Lists made so far (checks).
        public static IEnumerable<PlaylistAnnounceList> Lists { get { Prune(); return new List<PlaylistAnnounceList>(s_lists.Values); } }

        /// Use a SharedState per attraction (true) or keep lists in memory only (tests with no session).
        public static bool Shared = true;

        /// The attraction an object belongs to: the same object SharedState puts the attraction's bus on.
        public static GameObject RootOf(GameObject go)
        {
            if (go == null) return null;
            var level = go.GetComponentInParent<LevelTemplate>(true);
            if (level != null) return level.gameObject;
            var r = DreamParkLuaAPI.AttractionRoot(go);
            return r != null ? r : go.transform.root.gameObject;
        }

        /// The object's stable key under its attraction root.
        public static string KeyOf(GameObject go, GameObject root)
        {
            if (go == null) return null;
            if (root == null || go == root) return ".";
            var parts = new List<string>();
            for (var t = go.transform; t != null && t.gameObject != root; t = t.parent) parts.Add(Segment(t));
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string Strip(string n) => (n ?? "").Replace("(Clone)", "").Trim();

        /// "Name", or "Name~k" for the k-th (k ≥ 1) sibling with that name.
        static string Segment(Transform t)
        {
            string name = Strip(t.name);
            var p = t.parent;
            if (p == null) return name;
            int k = 0;
            for (int i = 0; i < p.childCount; i++)
            {
                var c = p.GetChild(i);
                if (c == t) break;
                if (Strip(c.name) == name) k++;
            }
            return k == 0 ? name : name + "~" + k;
        }

        public static PlaylistAnnounceList ListFor(GameObject go, bool create = true)
        {
            var root = RootOf(go);
            if (root == null) return null;
            int id = root.GetInstanceID();
            if (s_lists.TryGetValue(id, out var l) && l != null && !l.Store.Disposed && (UnityEngine.Object)(l.Root as GameObject) != null) return l;
            if (!create) return null;
            IAnnounceStore store = null;
            if (Shared && Application.isPlaying)
            {
                try { var ss = SharedState.Open(StateName, root, go); if (ss != null) store = new SharedAnnounceStore(ss); }
                catch (Exception e) { Debug.LogWarning("[pl-announce] shared list unavailable, local only: " + e.Message); }
            }
            l = new PlaylistAnnounceList(store ?? new MemoryAnnounceStore()) { Root = root, Name = root.name };
            s_lists[id] = l;
            Driver.Ensure();
            return l;
        }

        static void Prune()
        {
            List<int> dead = null;
            foreach (var kv in s_lists)
                if (kv.Value == null || kv.Value.Store.Disposed || (kv.Value.Root as GameObject) == null) (dead ??= new List<int>()).Add(kv.Key);
            if (dead != null) foreach (var k in dead) s_lists.Remove(k);
        }

        /// dp.announce(kind, {item, holds, from}) for `go`. Returns its key, or null (unknown kind / no attraction).
        public static string Announce(GameObject go, string kind, string item, IDictionary<string, int> holds, GameObject from)
        {
            if (go == null) return null;
            string k = AnnounceKinds.Normalize(kind);
            if (k == null) { Debug.LogWarning($"[pl-announce] {go.name}: unknown kind '{kind}' (collect, defeat, break, target, reach, find, protect, catch)"); return null; }
            var list = ListFor(go);
            if (list == null) return null;
            var tag = go.GetComponent<PlaylistAnnounceTag>();
            string key = tag != null && tag.list == list && !string.IsNullOrEmpty(tag.key) ? tag.key : KeyOf(go, list.Root as GameObject);
            string fromKey = null;
            if (from != null && (tag == null || string.IsNullOrEmpty(tag.key)))
            {
                var ft = from.GetComponent<PlaylistAnnounceTag>();
                fromKey = ft != null && ft.list == list ? ft.key : null;
                if (fromKey == null) Debug.LogWarning($"[pl-announce] {go.name}: its source {from.name} never announced (in this attraction): counted on its own");
            }
            string got = list.Announce(key, k, item, holds, fromKey, go, OrderOf(go, list.Root as GameObject));
            if (got == null) return null;
            if (tag == null) tag = go.AddComponent<PlaylistAnnounceTag>();
            tag.list = list; tag.key = got; tag.root = list.Root as GameObject;
            return got;
        }

        /// The fixed-number order of an object: cm from a point far out on the attraction's entry side (its local −Z), measured
        /// in the attraction root's own frame — front to back from the entry, the same on every headset (prefab-local).
        public static int OrderOf(GameObject go, GameObject root)
        {
            if (go == null || root == null) return -1;
            Vector3 l = root.transform.InverseTransformPoint(go.transform.position);
            float d = Vector2.Distance(new Vector2(l.x, l.z), new Vector2(0f, -30f));
            return Mathf.Clamp(Mathf.RoundToInt(d * 100f), 0, 999999);
        }

        /// A marker target's fixed number for `kind` (1…N, the same on every headset and after a reset), or 0.
        public static int NumberOf(GameObject target, string kind)
        {
            if (target == null) return 0;
            var tag = target.GetComponent<PlaylistAnnounceTag>();
            if (tag == null || tag.list == null) return 0;
            return tag.list.NumberOf(tag.key, kind);
        }

        /// dp.announce_done({by}) for `go` (by = a player id, or null).
        public static bool Done(GameObject go, string by)
        {
            if (go == null) return false;
            var tag = go.GetComponent<PlaylistAnnounceTag>();
            if (tag == null || tag.list == null) return false;
            return tag.list.Done(tag.key, string.IsNullOrEmpty(by) ? null : by);
        }

        /// The lists that belong to a stop root (the attraction is the root, inside it, or holds it).
        public static List<PlaylistAnnounceList> ListsUnder(GameObject stopRoot)
        {
            var l = new List<PlaylistAnnounceList>();
            if (stopRoot == null) return l;
            Prune();
            var rt = stopRoot.transform;
            foreach (var list in s_lists.Values)
            {
                var r = list.Root as GameObject;
                if (r == null) continue;
                if (r == stopRoot || r.transform.IsChildOf(rt) || rt.IsChildOf(r.transform)) l.Add(list);
            }
            return l;
        }

        /// stop.count{kind}: total / remaining / done / by player over the stop's attraction(s). kind null = every kind.
        public static PlaylistAnnounceCount Count(GameObject stopRoot, string kind)
        {
            var c = new PlaylistAnnounceCount();
            foreach (var l in ListsUnder(stopRoot)) c.Add(l.Count(kind));
            return c;
        }

        public static string CountJson(GameObject stopRoot, string kind) => Count(stopRoot, kind).ToJson();

        /// stop.mark{kind}: the objects to mark on THIS device (live, not done; holders still holding some).
        public static List<Transform> Live(GameObject stopRoot, string kind)
        {
            var l = new List<Transform>();
            foreach (var list in ListsUnder(stopRoot))
                foreach (var h in list.Live(kind))
                    if (h is GameObject g && g != null && g.activeInHierarchy) l.Add(g.transform);
            return l;
        }

        /// Any announcement under this stop root? (markers fall back to the census when none.)
        public static bool Any(GameObject stopRoot)
        {
            foreach (var l in ListsUnder(stopRoot)) if (l.LocalCount > 0) return true;
            return false;
        }

        // ── automatic done: switched off / destroyed while the attraction stays up ──
        static readonly List<(PlaylistAnnounceTag tag, PlaylistAnnounceList list, string key, GameObject root, GameObject go, int frame)> s_pending
            = new List<(PlaylistAnnounceTag, PlaylistAnnounceList, string, GameObject, GameObject, int)>();
        static bool s_quitting;

        internal static void Gone(PlaylistAnnounceTag tag)
        {
            if (s_quitting || !Application.isPlaying || tag == null || tag.list == null || string.IsNullOrEmpty(tag.key)) return;
            s_pending.Add((tag, tag.list, tag.key, tag.root, tag.gameObject, Time.frameCount));
            Driver.Ensure();
        }

        static void Settle()
        {
            if (s_pending.Count == 0) return;
            int f = Time.frameCount;
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                var p = s_pending[i];
                if (p.frame >= f) continue;   // one frame later: the attraction's own fate is known by then
                s_pending.RemoveAt(i);
                bool rootUp = p.root != null && p.root.activeInHierarchy;
                if (!rootUp) continue;                                           // packed away / unloading: not done
                bool objUp = p.tag != null && p.go != null && p.go.activeInHierarchy && p.tag.enabled;
                if (objUp) continue;                                             // back already (a quick toggle)
                if (p.list.Done(p.key, null)) Debug.Log($"[pl-announce] {p.list.Name}: {p.key} done (gone)");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Driver.Ensure();   // attractions announce their waypoints without any game code

        sealed class Driver : MonoBehaviour
        {
            static Driver s_inst;
            internal static void Ensure()
            {
                if (s_inst != null || !Application.isPlaying) return;
                var go = new GameObject("[PlaylistAnnounce]") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                s_inst = go.AddComponent<Driver>();
            }
            void OnEnable() { PlaylistEvents.Raised -= OnTitleEvent; PlaylistEvents.Raised += OnTitleEvent; }
            void OnDisable() { PlaylistEvents.Raised -= OnTitleEvent; }
            void Update()
            {
                try { Settle(); } catch (Exception e) { Debug.LogWarning("[pl-announce] settle: " + e.Message); }
                try { ScanAttractions(); } catch (Exception e) { Debug.LogWarning("[pl-announce] waypoints: " + e.Message); }
            }
            void OnApplicationQuit() { s_quitting = true; }
        }

        // ── waypoints (PLAYLIST-PLAN § 6.7) ─────────────────────────────────────────────────
        // Every placed attraction announces itself as a waypoint (its root, label = its display name) with no game code,
        // and again after its game resets it (reset_done). Games add their own with dp.announce('waypoint', {label}).

        /// A waypoint of an attraction now (this device): key, label, platform (= the attraction's own), done, its object.
        public sealed class Waypoint
        {
            public string key, label;
            public bool platform, done;
            public GameObject obj;
            public override string ToString() => $"{(platform ? "[attraction] " : "")}{label ?? key} {(done ? "done" : "open")}";
        }

        /// The waypoints of the attraction(s) under a stop root: the attraction's own first, then the game's.
        public static List<Waypoint> Waypoints(GameObject stopRoot)
        {
            var l = new List<Waypoint>();
            foreach (var list in ListsUnder(stopRoot))
                foreach (var w in list.Waypoints())
                    l.Add(new Waypoint { key = w.key, label = w.label, platform = w.platform, done = w.done, obj = w.handle as GameObject });
            return l;
        }

        /// The attraction counts as done for the map (§ 6.7 "the game announced it done"): its game waypoints are all done,
        /// or — with none — its own waypoint is (dp.announce_done from a script on the attraction root, or CompleteAttraction).
        public static bool WaypointDone(GameObject stopRoot)
        {
            var c = Count(stopRoot, AnnounceKinds.Waypoint);
            return c.total > 0 && c.remaining == 0;
        }

        /// Mark the attraction's own waypoint done (C# games / tools); by = a player id or null.
        public static bool CompleteAttraction(GameObject anyInAttraction, string by = null)
        {
            var root = RootOf(anyInAttraction);
            return root != null && Done(root, by);
        }

        static float s_nextScan;
        /// Announce every placed attraction's own waypoint (the driver, about once a second).
        static void ScanAttractions()
        {
            if (Time.unscaledTime < s_nextScan) return;
            s_nextScan = Time.unscaledTime + 1f;
            AttractionTemplate[] all;
            try { all = UnityEngine.Object.FindObjectsByType<AttractionTemplate>(FindObjectsSortMode.None); } catch (Exception) { return; }
            foreach (var at in all)
            {
                if (at == null || !at.isActiveAndEnabled) continue;
                var tag = at.GetComponent<PlaylistAnnounceTag>();
                if (tag != null && tag.list != null && !tag.list.Store.Disposed) continue;
                AnnounceAttraction(at);
            }
        }

        static void AnnounceAttraction(AttractionTemplate at)
        {
            string label = !string.IsNullOrWhiteSpace(at.displayName) ? at.displayName.Trim() : Humanize(at.name);
            Announce(at.gameObject, AnnounceKinds.Waypoint, label, null, null);
        }

        static string Humanize(string n)
        {
            n = (n ?? "").Replace("(Clone)", "").Trim();
            if (n.StartsWith("A_", StringComparison.Ordinal) || n.StartsWith("P_", StringComparison.Ordinal)) n = n.Substring(2);
            var sb = new StringBuilder();
            for (int i = 0; i < n.Length; i++)
            {
                char c = n[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(n[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// A game's reset (reset_done {occurrence}): the attraction announces its own waypoint again (a new generation when
        /// it was done).
        static void OnTitleEvent(PlaylistEvent e)
        {
            if (e == null || e.source != PlaylistEvent.Event || e.name != "reset_done") return;
            string occ = PlaylistRunPlan.Str(e.Data, "occurrence", null) ?? e.stop;
            if (string.IsNullOrEmpty(occ)) return;
            foreach (var list in new List<PlaylistAnnounceList>(s_lists.Values))
            {
                var root = list.Root as GameObject;
                if (root == null) continue;
                var m = root.GetComponentInParent<DreamParkPackageMembership>(true);
                if (m == null || m.occurrenceId != occ) continue;
                var at = root.GetComponent<AttractionTemplate>();
                if (at != null) { AnnounceAttraction(at); Debug.Log($"[pl-announce] {list.Name}: reset → its waypoint again"); }
            }
        }

        /// One line per list: counts per kind (logs / screenshots).
        public static string Describe(GameObject stopRoot)
        {
            var sb = new StringBuilder();
            foreach (var k in AnnounceKinds.All)
            {
                var c = Count(stopRoot, k);
                if (c.total == 0) continue;
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(k).Append(' ').Append(c.remaining).Append('/').Append(c.total);
            }
            return sb.ToString();
        }
    }

    /// Put on every announced object by PlaylistAnnounce: its key, and the automatic done.
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class PlaylistAnnounceTag : MonoBehaviour
    {
        [NonSerialized] public PlaylistAnnounceList list;
        [NonSerialized] public GameObject root;
        public string key;
        void OnDisable() => PlaylistAnnounce.Gone(this);
        void OnDestroy() => PlaylistAnnounce.Gone(this);
    }
}
