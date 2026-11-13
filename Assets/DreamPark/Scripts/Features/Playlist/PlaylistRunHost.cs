// ─────────────────────────────────────────────────────────────────────
//  PlaylistRunHost.cs — the Unity glue of PlaylistRun (repair-oct20 CONTRACT § 1–§ 4):
//    • ticks PlaylistRunWorld.Main (goal blocks / playlist Lua on the leader, forwarding elsewhere)
//    • the event bus → the runs (PlaylistEvents.Raised → Main.Ingest)
//    • the DreamieMoments seam: director = the run leader; other devices forward
//    • the runner's cues → platform Dreamie moments (stop_intro, stuck, stop_locked)
//    • on EVERY device, what the leader decided: goal MARKERS (PlaylistGoalMarkers, one batch per stop),
//      the CROSSING tracker (PlaylistJumpTracker: this device's wearer, start line → finish line, fails) and the
//      JUMP PADS (PlaylistJumpPads: pad → arch → pad hops) — the rules are PlaylistCrossingRules (pure)
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlaylistRunHost : MonoBehaviour
    {
        public static bool Enabled = true;
        static PlaylistRunHost s_inst;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (s_inst != null) return;
            var go = new GameObject("[PlaylistRunHost]") { hideFlags = HideFlags.HideInHierarchy };
            if (Application.isPlaying) DontDestroyOnLoad(go);
            s_inst = go.AddComponent<PlaylistRunHost>();
        }

        static PlaylistRun Lead()
        {
            foreach (var r in PlaylistRunWorld.Main.Runs) if (!r.Disposed && r.Current != null) return r;
            return null;
        }

        void OnEnable()
        {
            PlaylistEvents.Raised += OnEvent;
            PlaylistRunner.CueRaised += OnCue;
            DreamieMoments.IsDirector = () => { var r = Lead(); return r == null || r.IsLeader; };
            DreamieMoments.Forward = m => { var r = Lead(); if (r != null) r.SubmitMoment(m); else DreamieMoments.Accept(m); };
            DreamieMoments.Context = () =>
            {
                var r = Lead();
                var w = PlaylistRunRules.RoundWord(r?.Plan);
                return (r?.RunId, r?.Plan?.playlistId, w.one, w.many);
            };
            DreamieMoments.PlanContext = stop => PlaylistRunRules.DreamieContextJson(Lead()?.Plan, stop);
        }

        void OnDisable()
        {
            PlaylistEvents.Raised -= OnEvent;
            PlaylistRunner.CueRaised -= OnCue;
            if (s_inst == this) s_inst = null;
        }

        static void OnEvent(PlaylistEvent e) => PlaylistRunWorld.Main.Ingest(e);

        // The runner's local cues that ARE platform moments (same path as a title's dp.dreamie.moment).
        static readonly Dictionary<string, string> CueMoments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "stop_intro", "stop_intro" }, { "stuck", "stuck" }, { "stop_locked", "stop_locked" },
            { "boss_hint", "stuck" }, { "solve_hint", "stuck" },
        };

        static void OnCue(string name, string body)
        {
            if (name == null || !CueMoments.TryGetValue(name, out string moment)) return;
            var j = SharedState.ParseFragment(body);
            string stop = PlaylistRunPlan.Str(j, "occurrenceId", null);
            if (name != moment && j != null) j.SetField("hint", name);
            DreamieMoments.Moment(moment, PlaylistEvents.Clamp(j != null ? j.Print() : "{}", DreamieMoments.MaxDataBytes),
                priority: moment == "stop_intro" ? DreamieMoment.High : DreamieMoment.Normal, stop: string.IsNullOrEmpty(stop) ? null : stop,
                player: PlaylistRunPlan.Str(j, "playerId", null));
        }

        float _nextVisual;
        void Update()
        {
            if (!Enabled) return;
            PlaylistRunWorld.Main.Tick();
            if (Time.unscaledTime < _nextVisual) return;
            _nextVisual = Time.unscaledTime + 0.25f;
            try { Visuals(); } catch (Exception e) { Debug.LogWarning("[pl-runtime] visuals: " + e.Message); }
            try { PlaylistOutbox.Flush(); } catch (Exception) { }
        }

        // Markers and jumps render on EVERY device from the run's shared table.
        // Everything this host put on a stop root, so a stop that left the run (its round packed away, the run over —
        // Results / bookends) loses its visuals even when no spec change reaches it (take 4: the cannon's pin was still
        // "shown" through Results).
        static readonly PlaylistVisualSweep<Component> s_sweep = new PlaylistVisualSweep<Component>(c => c == null);

        static void Visuals()
        {
            s_sweep.Begin();
            foreach (var run in PlaylistRunWorld.Main.Runs)
            {
                if (run == null || run.Disposed) continue;
                var leg = run.Current;
                if (leg == null) continue;
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
                {
                    if (s == null) continue;
                    var root = leg.StopRoot(s);
                    if (root == null) continue;
                    string occ = s.occurrenceId;
                    string mark = run.MarkSpec(occ);
                    var gm = root.GetComponent<PlaylistGoalMarkers>();
                    Seen(gm); Seen(root.GetComponent<PlaylistJumpTracker>()); Seen(root.GetComponent<PlaylistJumpPads>()); Seen(root.GetComponent<PlaylistReachTracker>());
                    if (mark != null)
                    {
                        if (gm == null) gm = root.AddComponent<PlaylistGoalMarkers>();
                        Seen(gm);
                        gm.Spec(run.RunId, occ, mark);
                    }
                    else if (gm != null) gm.Spec(run.RunId, occ, null);
                    // jp.{occ}: a crossing (stop.crossing) or jump pads (stop.jump{pads}, "kind":"jump").
                    string jump = run.JumpSpec(occ);
                    bool pads = jump != null && jump.Contains("\"kind\":\"jump\"");
                    bool reach = jump != null && jump.Contains("\"kind\":\"reach\"");
                    var rt = root.GetComponent<PlaylistReachTracker>();
                    if (reach)
                    {
                        if (rt == null) rt = root.AddComponent<PlaylistReachTracker>();
                        Seen(rt);
                        rt.Spec(occ, jump);
                    }
                    else if (rt != null) rt.Stop();
                    if (reach) { var jt0 = root.GetComponent<PlaylistJumpTracker>(); if (jt0 != null) jt0.Stop(); continue; }
                    var jt = root.GetComponent<PlaylistJumpTracker>();
                    if (jump != null && !pads)
                    {
                        if (jt == null) jt = root.AddComponent<PlaylistJumpTracker>();
                        Seen(jt);
                        jt.Spec(run, occ, jump, run.JumpResetSpec(occ));
                    }
                    else if (jt != null) jt.Stop();
                    var jpads = root.GetComponent<PlaylistJumpPads>();
                    if (pads)
                    {
                        if (jpads == null) jpads = root.AddComponent<PlaylistJumpPads>();
                        Seen(jpads);
                        jpads.Spec(run, occ, jump);
                    }
                    else if (jpads != null) jpads.Stop();
                }
            }
            Sweep();
        }

        static void Seen(Component c) { if (c != null) s_sweep.Seen(c); }

        /// Tear down what no live run's current round still shows.
        static int Sweep()
        {
            int n = 0;
            foreach (var c in s_sweep.End())
            {
                n++;
                switch (c)
                {
                    case PlaylistGoalMarkers gm: gm.Spec(null, null, null); break;
                    case PlaylistJumpTracker jt: jt.Stop(); break;
                    case PlaylistJumpPads jp: jp.Stop(); break;
                    case PlaylistReachTracker rt: rt.Stop(); break;
                }
            }
            return n;
        }
    }

    /// What the in-world goal UI may show (goal-ui, Oct 20 2026; Aidan's Mole Hill Showdown review): the core answers
    /// "is a wall between the eye and this point?" (scanned walls / the room graph) and "is a big panel up?" (the hold
    /// board / Results). With no core (an SDK title's own editor) nothing is blocked and no panel is up.
    public static class PlaylistGoalSight
    {
        /// (eye, target), world: true when a real wall stands between them. Null = never blocked.
        public static Func<Vector3, Vector3, bool> Blocked;
        /// True while a big panel owns the view (the start board, Results, standings). Null = never.
        public static Func<bool> PanelUp;

        public static bool CanSee(Vector3 eye, Vector3 target)
        {
            try { return Blocked == null || !Blocked(eye, target); } catch (Exception) { return true; }
        }

        // ── HUD / UI over the markers (Aidan, Oct 22: "the coin pins draw over SAL's challenge HUD strip and its text blast") ──
        // Objective markers are the LOWEST UI: every HUD and panel draws over them. Two parts:
        //   • draw order: marker renderers use sorting order MarkerSortingOrder (< 0), so any transparent HUD / panel (a title's
        //     world-space HUD at order 0, MagicOnTop panels at 20000+) draws after them, wherever it is;
        //   • screen area: a pin whose spot is inside a NEARER HUD's screen rect fades out (opaque HUD parts — SAL's real 3D
        //     coins — draw before any transparent, so order alone cannot keep a pin from poking through them).
        // What counts as HUD: roots a title marks (dp.hud(go) / Hud(t)), every MagicOnTop panel, anything under the head
        // camera (head-locked), world-space canvases outside attractions, and scene-root objects named "…HUD…".
        // Markers stay hidden behind walls (Blocked above) exactly as approved.
        public const int MarkerSortingOrder = -40;

        static readonly HashSet<Transform> s_hud = new HashSet<Transform>();
        /// Mark (or unmark) a title's HUD root: everything under it draws over the objective markers, which fade where it is.
        public static void Hud(Transform root, bool on = true)
        {
            if (root == null) return;
            if (on) s_hud.Add(root); else s_hud.Remove(root);
            s_rectsAt = -1f;
        }

        static readonly List<(Rect r, float near)> s_rects = new List<(Rect, float)>();
        static float s_rectsAt = -1f;
        static Camera s_rectsCam;
        static readonly List<Transform> s_roots = new List<Transform>();
        static readonly Vector3[] s_c = new Vector3[8];

        /// The screen (viewport) rects of every HUD / UI element in front of `cam` now, with their nearest depth (m).
        /// Refreshed at most every 0.2 s.
        public static List<(Rect r, float near)> HudRects(Camera cam)
        {
            if (cam == null) { s_rects.Clear(); return s_rects; }
            if (cam == s_rectsCam && s_rectsAt >= 0f && Time.unscaledTime - s_rectsAt < 0.2f) return s_rects;
            s_rectsCam = cam; s_rectsAt = Time.unscaledTime;
            s_rects.Clear(); s_roots.Clear();
            s_hud.RemoveWhere(t => t == null);
            foreach (var t in s_hud) s_roots.Add(t);
            try { foreach (var p in UnityEngine.Object.FindObjectsByType<MagicUI.MagicOnTop>(FindObjectsSortMode.None)) if (p != null) s_roots.Add(p.transform); } catch (Exception) { }
            if (cam.transform.childCount > 0) s_roots.Add(cam.transform);
            try
            {
                foreach (var cv in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                    if (cv != null && cv.isRootCanvas && cv.renderMode == RenderMode.WorldSpace && cv.GetComponentInParent<LevelTemplate>(true) == null) s_roots.Add(cv.transform);
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (!sc.isLoaded) continue;
                    foreach (var g in sc.GetRootGameObjects())
                        if (g != null && g.name.IndexOf("HUD", StringComparison.Ordinal) >= 0 && g.GetComponent<LevelTemplate>() == null) s_roots.Add(g.transform);
                }
            }
            catch (Exception) { }
            foreach (var root in s_roots)
            {
                if (root == null || !root.gameObject.activeInHierarchy) continue;
                bool any = false; float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f, near = 1e9f;
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (IsMarker(r.transform) || SoftLight(r)) continue;
                    // the renderer's own (oriented) box: a camera-facing quad projects to itself, not to its world AABB
                    Bounds b = r.localBounds; Vector3 c = b.center, e = b.extents;
                    Matrix4x4 m = r.transform.localToWorldMatrix;
                    int k = 0;
                    for (int sx = -1; sx <= 1; sx += 2) for (int sy = -1; sy <= 1; sy += 2) for (int sz = -1; sz <= 1; sz += 2) s_c[k++] = m.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                    foreach (var w in s_c)
                    {
                        Vector3 v = cam.WorldToViewportPoint(w);
                        if (v.z <= 0.02f) continue;
                        any = true;
                        x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x); y0 = Mathf.Min(y0, v.y); y1 = Mathf.Max(y1, v.y); near = Mathf.Min(near, v.z);
                    }
                }
                if (any && x1 > 0f && x0 < 1f && y1 > 0f && y0 < 1f) s_rects.Add((Rect.MinMaxRect(x0, y0, x1, y1), near));
            }
            return s_rects;
        }

        /// Soft additive light (glows, halos, sparkles: particle / additive shaders) never hides what is behind it, so it is
        /// not HUD area. Panel glass (DreamPark/HoloParticle) is.
        static bool SoftLight(Renderer r)
        {
            var sh = r.sharedMaterial != null ? r.sharedMaterial.shader : null;
            string n = sh != null ? sh.name : "";
            return n.IndexOf("Particles", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsMarker(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (p.name == "GoalMarker") return true;
            return false;
        }

        /// True when `world` is on screen inside a HUD / UI element nearer to the viewer (the marker there fades).
        public static bool BehindHud(Camera cam, Vector3 world, float margin = 0.02f)
        {
            if (cam == null) return false;
            Vector3 v = cam.WorldToViewportPoint(world);
            if (v.z <= 0f) return false;
            foreach (var (r, near) in HudRects(cam))
                if (near < v.z && v.x >= r.xMin - margin && v.x <= r.xMax + margin && v.y >= r.yMin - margin && v.y <= r.yMax + margin) return true;
            return false;
        }

        /// Any panel up that goal markers must step back from (UI never stacks): a stop / objective card, the core's
        /// big boards. The lock sign is handled per marker (only the markers it covers step back).
        public static bool AnyPanelUp()
        {
            if (PlaylistObjectiveCallout.AnyCardUp) return true;
            try { if (PlaylistObjectiveCallout.HoldOff != null && PlaylistObjectiveCallout.HoldOff()) return true; } catch (Exception) { }   // the round diorama
            try { return PanelUp != null && PanelUp(); } catch (Exception) { return false; }
        }
    }

    /// stop.mark{kind=…}: every announced object of that kind in the stop (PlaylistAnnounce, PLAYLIST-PLAN § 4.2) gets
    /// the kind's generic white icon, and the nearest carries what is left of it (announce Count: held items and
    /// hand-offs included). stop.mark{at=pos, icon=…}: a spot (icon='target' = the shared target circle on the floor).
    /// DEPRECATED, kept until the titles announce: stop.mark{script=…|marker=…|name=…, icon=…, number=…} (census by
    /// script / PlaylistObjectiveMarker / name glob).
    /// Universal goal markers (DECISIONS: a flattish triangle arrow + number and/or goal icon, Magic UI light language),
    /// ONE component (one batch) per stop; each stop.mark call is a group.
    /// Oct 20 (goal-ui, Aidan's review of the Mole Hill Showdown take):
    ///   • screen-space size: the pin's ANGULAR size shrinks as you come close (≈ 12° tall from 5 m, ≈ 5° at 0.8 m), so
    ///     up close it stays small and on screen, from across the room it reads;
    ///   • a flatter chevron, the icon and number stacked tight over it;
    ///   • the count shows ONCE per group, anchored to one target with hysteresis (kept until that target is gone or
    ///     well out of view), never hopping between coins;
    ///   • right over the target's WORLD bounds (its renderers + colliders and whatever of the attraction encloses it —
    ///     the Mole Hill Cannon's shell), never at its pivot;
    ///   • hidden (a short fade) when a wall stands between the player and the target (PlaylistGoalSight), while a
    ///     card / board is up, and where a lock sign covers it.
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlaylistGoalMarkers : MonoBehaviour
    {
        /// Approved look (Aidan, Oct 20: "exactly!"): arrow + icon + the count, ALL ONE COLOUR, white.
        public const float Gap = 0.035f, ArrowSize = 0.2f, MaxScale = 4f;
        /// The chevron's height / width (Oct 20 review: "squash the arrow vertically").
        public const float ArrowH = 0.36f;
        /// Local layout (× ArrowSize): icon centre and size over the chevron, the number over the icon (tight stack).
        public const float IconY = 0.74f, IconS = 0.95f, LabelYIcon = 1.66f, LabelYBare = 0.7f;
        /// The whole pin's height (× ArrowSize, chevron bottom → number top) for the angular size.
        public const float PinUnits = 2.15f;
        /// Angular height of the whole pin: FarDeg from FarM on, easing down to NearDeg at NearM (smaller up close).
        public const float FarDeg = 12f, NearDeg = 5f, FarM = 5f, NearM = 0.8f;
        /// Gone inside HideM (standing on it). The number is part of the pin: it shows whenever its pin shows and
        /// shrinks with it (Aidan, Oct 21: "why doesn't it just keep showing up like the rest of it?").
        public const float HideM = 0.45f, FadeS = 0.25f;

        /// The pin's scale at a viewer distance (pure; 0 = hidden).
        public static float ScaleAt(float dist)
        {
            if (dist <= HideM) return 0f;
            float k = Mathf.Clamp01((dist - NearM) / (FarM - NearM));
            float deg = Mathf.Lerp(NearDeg, FarDeg, k * k * (3f - 2f * k));
            float h = 2f * dist * Mathf.Tan(deg * 0.5f * Mathf.Deg2Rad);                     // metres tall at that angle
            float s = Mathf.Min(MaxScale, h / (ArrowSize * PinUnits));
            float inner = Mathf.Clamp01((dist - HideM) / (NearM - HideM));
            return s * inner * inner * (3f - 2f * inner);
        }

        /// The pin's angular height (degrees) at a viewer distance — for checks.
        public static float AngleAt(float dist) => dist <= 0f ? 0f : Mathf.Rad2Deg * 2f * Mathf.Atan(ArrowSize * PinUnits * ScaleAt(dist) * 0.5f / dist);
        public static readonly Color Ink = new Color(1f, 1f, 1f, 0.96f);
        /// Draw over the attraction's geometry (a Jubbo behind a block still shows its marker). Walls hide it (Sight).
        public static bool Xray = true;

        sealed class Group
        {
            public PlaylistCensusFilter filter;
            public readonly List<Vector3> at = new List<Vector3>();
            public string icon, number;
            /// An announce kind group (stop.mark{kind}): its count is the announce list's remaining.
            public string kind;
            public int remaining;
            /// Kind groups: each target's fixed number (refreshed at every resolve).
            public readonly Dictionary<Transform, int> numbers = new Dictionary<Transform, int>();
            public bool circle = true;
            public float radius = PlaylistTargetCircle.DefaultRadius;   // icon 'target' / 'zone' on `at` spots draws the target circle (circle=false: the pin only)
            public Color tint;
            public readonly List<Transform> targets = new List<Transform>();
            public readonly List<Pin> pins = new List<Pin>();
            public readonly List<PlaylistTargetCircle> circles = new List<PlaylistTargetCircle>();
            public Material arrow, glow;
            // The count's anchor (hysteresis): a target transform, or an `at` index; when it went out of view.
            public Transform anchorT; public int anchorAt = -1;
            public int anchorIndex = -1;
        }

        sealed class Pin
        {
            public GameObject go;
            public Transform arrow, glow, icon;
            public TMPro.TextMeshPro label;
            public Renderer[] rends;
            public float vis = 1f;           // eased visibility 0..1 (walls, panels, lock sign, a nearer pin over it)
            public float lastAlpha = -1f;
            // last frame on screen (pixels): centre, radius, distance — the nearer-pin occlusion test
            public Vector2 sc; public float sr, dist; public bool drawn, occluded;
        }

        string _spec, _occ;
        readonly List<Group> _groups = new List<Group>();
        float _nextResolve, _nextSight;
        static Mesh s_arrow;
        static Material s_arrowBase, s_glowBase;
        static readonly Dictionary<string, Material> s_iconMats = new Dictionary<string, Material>();
        static MaterialPropertyBlock s_mpb;
        // A target's marker point = its own position + this offset (measured at each resolve; followed every frame).
        readonly Dictionary<Transform, Vector3> _topOffset = new Dictionary<Transform, Vector3>();
        readonly Dictionary<Transform, bool> _seen = new Dictionary<Transform, bool>();
        readonly Dictionary<int, bool> _seenAt = new Dictionary<int, bool>();
        readonly Dictionary<Transform, int> _stack = new Dictionary<Transform, int>();

        /// Live markers (all groups) — checks / screenshots.
        public int Count { get { int n = 0; foreach (var g in _groups) n += g.targets.Count + g.at.Count; return n; } }
        public IEnumerable<Transform> Targets { get { foreach (var g in _groups) foreach (var t in g.targets) if (t) yield return t; } }
        /// Pins drawn now (visible, scaled > 0) and how many of them carry a number — for checks.
        public int Shown { get; private set; }
        public int Numbered { get; private set; }
        /// The world points the pins sit over now (checks / screenshots), and the count's anchor index per group.
        public readonly List<Vector3> Points = new List<Vector3>();
        public IEnumerable<PlaylistTargetCircle> Circles { get { foreach (var g in _groups) foreach (var c in g.circles) if (c) yield return c; } }

        /// The leader's spec for this stop: a group object or an array of groups (null = no markers).
        public void Spec(string runId, string occ, string json)
        {
            _occ = occ;
            if (json == _spec) return;
            _spec = json;
            Clear();
            if (json == null) return;
            var j = SharedState.ParseFragment(json);
            var list = new List<JSONObject>();
            if (j != null && j.type == JSONObject.Type.Array && j.list != null) list.AddRange(j.list);
            else if (j != null && j.type == JSONObject.Type.Object) list.Add(j);
            foreach (var g in list)
            {
                if (g == null || g.type != JSONObject.Type.Object) continue;
                if (!g.HasField("inside")) g.AddField("inside", true);   // markers point at what still counts
                var grp = new Group { filter = PlaylistCensusFilter.FromJson(g.Print()), icon = PlaylistRunPlan.Str(g, "icon", null),
                    circle = g.GetField("circle") == null || g.GetField("circle").type != JSONObject.Type.Bool || g.GetField("circle").boolValue };
                grp.kind = grp.filter.kind;
                if (grp.kind != null && grp.icon == null) grp.icon = grp.kind;   // the kind's generic icon
                if (g.GetField("radius") != null && g.GetField("radius").type == JSONObject.Type.Number) grp.radius = Mathf.Clamp((float)g.GetField("radius").doubleValue, 0.25f, 3f);
                var at = g.GetField("at");
                if (at != null)
                {
                    if (at.type == JSONObject.Type.Object) grp.at.Add(V(at));
                    else if (at.type == JSONObject.Type.Array && at.list != null)
                        foreach (var a in at.list) if (a != null && a.type == JSONObject.Type.Object) grp.at.Add(V(a));
                }
                var num = g.GetField("number");
                grp.number = num == null ? null : num.type == JSONObject.Type.String ? num.stringValue : num.type == JSONObject.Type.Number ? PlaylistRunRules.Num(num.doubleValue) : null;
                if (grp.kind != null && num == null) grp.number = "remaining";   // the approved look: the count on the nearest
                grp.tint = Ink;   // one colour for every element (approved Oct 20)
                _groups.Add(grp);
            }
            _nextResolve = 0; _nextSight = 0;
            Debug.Log($"[pl-runtime] markers at {occ}: {_groups.Count} group(s) {json}");
        }

        static Vector3 V(JSONObject a) => new Vector3((float)PlaylistRunPlan.Num(a, "x"), (float)PlaylistRunPlan.Num(a, "y"), (float)PlaylistRunPlan.Num(a, "z"));

        void Clear()
        {
            foreach (var g in _groups)
            {
                foreach (var p in g.pins) if (p.go != null) Destroy(p.go);
                foreach (var c in g.circles) if (c != null) Destroy(c.gameObject);
                if (g.arrow != null) Destroy(g.arrow);
                if (g.glow != null) Destroy(g.glow);
            }
            _groups.Clear();
            _topOffset.Clear(); _seen.Clear(); _seenAt.Clear();
            Shown = Numbered = 0; Points.Clear();
        }

        bool IsCircle(Group g) => (g.icon == "target" || g.icon == "zone") && g.circle;

        void Update()
        {
            if (_groups.Count == 0) { Shown = Numbered = 0; Points.Clear(); return; }
            bool resolve = Time.unscaledTime >= _nextResolve;
            if (resolve) _nextResolve = Time.unscaledTime + 0.4f;
            bool sight = Time.unscaledTime >= _nextSight;
            if (sight) _nextSight = Time.unscaledTime + 0.15f;
            var head = PlaylistObjectiveCallout.Head();
            var tpl = GetComponentInChildren<AttractionTemplate>(true);
            Transform frame = tpl != null ? tpl.transform : transform;
            float t = Time.time;
            bool panel = PlaylistGoalSight.AnyPanelUp();
            Camera cam = head != null ? head.GetComponent<Camera>() : null;   // explicit: GetComponent's editor fake-null breaks ??
            if (cam == null) cam = Camera.main;
            int shown = 0, numbered = 0;
            Points.Clear();
            _stack.Clear();
            // Pins drawn last frame (all groups): a pin a NEARER pin overlaps on screen steps back (Aidan, Oct 22: no stacks).
            _drawn.Clear();
            foreach (var g0 in _groups) foreach (var p0 in g0.pins) if (p0.drawn) _drawn.Add(p0);
            foreach (var g in _groups)
            {
                if (resolve)
                {
                    g.targets.Clear();
                    if (g.filter != null && !g.filter.Empty) foreach (var go in PlaylistCensus.Find(gameObject, g.filter)) g.targets.Add(go.transform);
                    foreach (var tr in g.targets) _topOffset[tr] = Top(tr, transform) - tr.position;
                    if (g.kind != null)
                    {
                        g.remaining = PlaylistAnnounce.Count(gameObject, g.kind).remaining;
                        g.numbers.Clear();
                        foreach (var tr in g.targets) g.numbers[tr] = PlaylistAnnounce.NumberOf(tr.gameObject, g.kind);
                    }
                }
                // A pinned target collected / killed between resolves (destroyed = Unity fake-null, or switched off):
                // its pin drops NOW, never a MissingReferenceException per frame.
                g.targets.RemoveAll(x => !x || !x.gameObject.activeInHierarchy);
                int n = g.targets.Count + g.at.Count;
                while (g.pins.Count < n) g.pins.Add(MakePin(g));
                var tops = new List<Vector3>(n);
                var stacked = new List<int>(n);
                for (int i = 0; i < n; i++)
                {
                    Vector3 top;
                    int st = 0;
                    if (i < g.targets.Count)
                    {
                        var tr = g.targets[i];
                        // The same object in two groups (a carrier Jubbo: defeat it AND it holds a coin): this pin
                        // stacks above the earlier group's, never on top of it.
                        st = _stack.TryGetValue(tr, out int sc) ? sc : 0;
                        _stack[tr] = st + 1;
                        top = tr.position + (_topOffset.TryGetValue(tr, out var off) ? off : Vector3.up * 0.3f);
                    }
                    else
                    {
                        Vector3 floor = frame.TransformPoint(g.at[i - g.targets.Count]);
                        // A target circle: the pin floats over the circle at chest height (the circle owns the floor).
                        top = floor + Vector3.up * (IsCircle(g) ? 1.15f : 0.1f);
                    }
                    tops.Add(top);
                    stacked.Add(st);
                }
                // Target circles for `at` spots (one per spot, made once).
                if (IsCircle(g))
                {
                    while (g.circles.Count < g.at.Count)
                    {
                        Vector3 floor = frame.TransformPoint(g.at[g.circles.Count]);
                        g.circles.Add(PlaylistTargetCircle.Create(transform, floor, g.radius));
                    }
                }
                // Line of sight (walls / another room), every 150 ms.
                if (sight && head != null)
                    for (int i = 0; i < n; i++)
                    {
                        bool see = PlaylistGoalSight.CanSee(head.position, tops[i]);
                        if (i < g.targets.Count) _seen[g.targets[i]] = see; else _seenAt[Key(g, i - g.targets.Count)] = see;
                    }
                bool Seen(int i) => i < g.targets.Count ? (!_seen.TryGetValue(g.targets[i], out bool s) || s) : (!_seenAt.TryGetValue(Key(g, i - g.targets.Count), out bool s2) || s2);
                for (int c = 0; c < g.circles.Count; c++)
                    if (g.circles[c] != null) g.circles[c].SightTarget = Seen(g.targets.Count + c) ? 1f : 0f;
                int anchor = g.number == "remaining" ? Anchor(g, tops, head) : -1;
                for (int i = 0; i < g.pins.Count; i++)
                {
                    var p = g.pins[i];
                    bool exists = i < n;
                    if (p.go.activeSelf != exists) p.go.SetActive(exists);
                    if (!exists) { p.vis = 0f; continue; }
                    // Right over the target: the arrow's tip a finger's breadth above its top; the pin keeps a readable
                    // angular size and shrinks as you come close.
                    float dist = head != null ? Vector3.Distance(head.position, tops[i]) : FarM;
                    float k0 = ScaleAt(dist);
                    Vector3 mid = tops[i] + Vector3.up * (Gap * k0 + ArrowSize * PinUnits * 0.5f * k0 + stacked[i] * ArrowSize * PinUnits * k0);
                    p.dist = dist; p.drawn = false;
                    bool onScreen = false;
                    if (cam != null)
                    {
                        Vector3 c0 = cam.WorldToScreenPoint(mid), c1 = cam.WorldToScreenPoint(mid + Vector3.up * (ArrowSize * PinUnits * 0.5f * k0));
                        onScreen = c0.z > 0f;
                        p.sc = new Vector2(c0.x, c0.y); p.sr = Mathf.Max(2f, Vector2.Distance(p.sc, new Vector2(c1.x, c1.y)));
                    }
                    p.occluded = !LegacyPins && onScreen && OverlapsNearer(p);
                    bool want = !panel && Seen(i) && !p.occluded && !(head != null && PlaylistLockField.SignCovers(head.position, tops[i]))
                        && !PlaylistGoalSight.BehindHud(cam, mid);   // under a HUD: fade
                    p.vis = Mathf.MoveTowards(p.vis, want ? 1f : 0f, Time.unscaledDeltaTime / FadeS);
                    float k = ScaleAt(dist) * (0.6f + 0.4f * p.vis);
                    if (k <= 0.001f || p.vis <= 0.001f) { p.go.transform.localScale = Vector3.zero; p.go.SetActive(false); continue; }   // standing on it / hidden: no live pin
                    p.go.transform.localScale = Vector3.one * k;
                    float bob = Mathf.Abs(Mathf.Sin(t * 2.4f + i * 0.9f)) * 0.02f * k;
                    p.go.transform.position = tops[i] + Vector3.up * (Gap * k + ArrowSize * ArrowH * 0.5f * k + bob + stacked[i] * ArrowSize * PinUnits * k);
                    if (head != null)
                    {
                        Vector3 to = head.position - p.go.transform.position; to.y = 0;
                        if (to.sqrMagnitude > 1e-4f) p.go.transform.rotation = Quaternion.LookRotation(-to.normalized, Vector3.up);
                    }
                    float dim = LegacyPins ? 1f : DistanceDim(dist);   // farther pins dimmer: depth always reads
                    Fade(p, p.vis * dim);
                    p.drawn = p.vis > 0.02f;
                    float pulse = 0.9f + 0.1f * Mathf.Sin(t * 4f + i);
                    p.glow.localScale = Vector3.one * (ArrowSize * 1.8f * pulse);
                    // Kind groups: every pin its own fixed number (1…N per kind, stable across headsets and resets).
                    string text;
                    if (g.kind != null && LegacyPins) text = i == anchor ? g.remaining.ToString() : "";
                    else if (g.kind != null) text = i < g.targets.Count && g.numbers.TryGetValue(g.targets[i], out int num) && num > 0 ? num.ToString() : "";
                    else text = g.number == "remaining" ? (i == anchor ? n.ToString() : "") : g.number;
                    if (p.label != null)
                    {
                        p.label.text = text ?? "";
                        p.label.alpha = p.vis * dim;
                        bool on = !string.IsNullOrEmpty(text);
                        if (p.label.gameObject.activeSelf != on) p.label.gameObject.SetActive(on);
                        if (on) numbered++;
                    }
                    shown++;
                    Points.Add(tops[i]);
                }
            }
            Shown = shown; Numbered = numbered;
        }

        static int Key(Group g, int atIndex) => (g.GetHashCode() * 31) ^ atIndex;

        readonly List<Pin> _drawn = new List<Pin>();
        /// Harness before/after only: the pre-Oct-22 pins (stacking, no distance dim, the remaining count on the nearest).
        public static bool LegacyPins = false;
        /// Overlap a pin must reach to step back behind a nearer one, and clear to come back (hysteresis: no flicker).
        public const float OverlapIn = 0.75f, OverlapOut = 0.95f, NearerByM = 0.05f;
        bool OverlapsNearer(Pin p)
        {
            foreach (var q in _drawn)
            {
                if (q == p || q.dist >= p.dist - NearerByM) continue;
                float lim = (p.occluded ? OverlapOut : OverlapIn) * (p.sr + q.sr);
                if (Vector2.Distance(p.sc, q.sc) < lim) return true;
            }
            return false;
        }

        /// 1 up to 3 m, easing to 0.45 by 12 m.
        public static float DistanceDim(float dist) => Mathf.Lerp(1f, 0.45f, SS(3f, 12f, dist));
        /// Pins that stepped back behind a nearer pin now (checks).
        public int Occluded { get { int n = 0; foreach (var g in _groups) foreach (var p in g.pins) if (p.go != null && p.occluded) n++; return n; } }

        /// The pin that carries the group's count: it stays on the SAME target until that target is collected or gone,
        /// then moves to the nearest remaining one. Never re-anchored for view or walls (Aidan, Oct 21: the number
        /// "randomly pops in and out"); it simply shows and hides with its own pin.
        int Anchor(Group g, List<Vector3> tops, Transform head)
        {
            int n = tops.Count;
            if (n == 0) { g.anchorT = null; g.anchorAt = -1; return g.anchorIndex = -1; }
            int cur = -1;
            if (g.anchorT != null) cur = g.targets.IndexOf(g.anchorT);
            else if (g.anchorAt >= 0 && g.anchorAt < g.at.Count) cur = g.targets.Count + g.anchorAt;
            if (cur >= 0) return g.anchorIndex = cur;
            int best = 0; float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d = head != null ? (tops[i] - head.position).sqrMagnitude : i;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < g.targets.Count) { g.anchorT = g.targets[best]; g.anchorAt = -1; }
            else { g.anchorT = null; g.anchorAt = best - g.targets.Count; }
            return g.anchorIndex = best;
        }

        /// Per-pin fade over the group's shared materials (property blocks keep the batch's materials).
        static void Fade(Pin p, float a)
        {
            if (p.rends == null || Mathf.Abs(p.lastAlpha - a) < 0.01f) return;
            p.lastAlpha = a;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            foreach (var r in p.rends)
            {
                if (r == null) continue;
                var m = r.sharedMaterial;
                if (m == null || !m.HasProperty("_BaseColor")) continue;
                Color c = m.GetColor("_BaseColor"); c.a *= a;
                r.GetPropertyBlock(s_mpb);
                s_mpb.SetColor("_BaseColor", c);
                r.SetPropertyBlock(s_mpb);
            }
        }

        /// Right over the target's WORLD bounds: its own renderers + colliders (within 1.5 m of it, never a far child
        /// such as a patrol point), above any label it wears (a boss's name + health bar), and above whatever of the
        /// attraction ENCLOSES it (Oct 20: the Mole Hill Cannon's marker sat inside the cannon — its script lives on a
        /// child inside the shell). The highest collider of the stop over that spot (a ray from above) wins too.
        static Vector3 Top(Transform t, Transform stopRoot)
        {
            if (!t) return Vector3.zero;   // Unity null (destroyed): callers prune first
            bool any = false; Bounds b = default;
            float labelTop = float.MinValue;
            Vector3 at = t.position;
            void Take(Bounds rb) { if (!any) { b = rb; any = true; } else b.Encapsulate(rb); }
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (IsOurs(r.transform)) continue;
                Bounds rb = r.bounds;
                Vector3 d = rb.center - at; d.y = 0;
                if (d.magnitude > 1.5f) continue;
                if (r.GetComponent<TMPro.TMP_Text>() != null) { labelTop = Mathf.Max(labelTop, rb.max.y); continue; }
                Take(rb);
            }
            foreach (var c in t.GetComponentsInChildren<Collider>(false))
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                Bounds cb = c.bounds;
                Vector3 d = cb.center - at; d.y = 0;
                if (d.magnitude > 1.5f || cb.size.sqrMagnitude > 36f) continue;
                Take(cb);
            }
            Vector3 centre = any ? b.center : at;
            float top = any ? b.max.y : at.y + 0.3f;
            // Whatever of the attraction encloses the target (a shell / body it sits inside): renderers no wider than
            // 4 m whose box holds the target's centre (horizontally) and reaches above its top.
            if (stopRoot != null)
            {
                foreach (var r in stopRoot.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (r.transform.IsChildOf(t) || IsOurs(r.transform) || r.GetComponent<TMPro.TMP_Text>() != null) continue;
                    Bounds rb = r.bounds;
                    if (rb.size.x > 4f || rb.size.z > 4f || rb.max.y <= top || rb.min.y > top - 0.05f) continue;
                    Vector3 c = rb.center, e = rb.extents * 0.8f;
                    if (Mathf.Abs(centre.x - c.x) > e.x || Mathf.Abs(centre.z - c.z) > e.z) continue;
                    // It must hold the target, not stand beside it: the target's centre is inside this box vertically.
                    if (centre.y < rb.min.y || centre.y > rb.max.y) continue;
                    top = Mathf.Min(rb.max.y, top + 3f);
                }
                // The stop's own colliders straight above the spot (a curved dome's real surface).
                try
                {
                    var hits = Physics.RaycastAll(new Vector3(centre.x, top + 3f, centre.z), Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
                    foreach (var h in hits)
                        if (h.collider != null && h.collider.transform.IsChildOf(stopRoot) && !IsOurs(h.collider.transform) && h.point.y > top
                            && h.collider.bounds.min.y <= centre.y + 0.05f && h.point.y < top + 3f)
                            top = h.point.y;
                }
                catch (Exception) { }
            }
            // The boss's own HUD (its name + health bar: TMP labels, or anything under a `*health-bar*` / `*hud*` script)
            // floating over it, even when it is not the target's child (SAL: BossBar at the attraction root): the pin
            // goes above it, never across it.
            if (stopRoot != null)
            {
                foreach (var r in stopRoot.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled || r is ParticleSystemRenderer || IsOurs(r.transform)) continue;
                    if (r.GetComponent<TMPro.TMP_Text>() == null && !UnderHud(r.transform, stopRoot)) continue;
                    Bounds rb = r.bounds;
                    Vector3 d = rb.center - centre; d.y = 0;
                    if (d.magnitude > 1.5f || rb.max.y < top - 0.05f || rb.min.y > top + 2.5f) continue;
                    labelTop = Mathf.Max(labelTop, rb.max.y);
                }
            }
            if (labelTop > float.MinValue) top = Mathf.Max(top, labelTop + 0.04f);
            return new Vector3(centre.x, top, centre.z);
        }

        static bool UnderHud(Transform x, Transform stopRoot)
        {
            for (var p = x; p != null && p != stopRoot; p = p.parent)
            {
                var lb = p.GetComponent<LuaBehaviour>();
                string n = lb != null && lb.luaScript != null ? lb.luaScript.name : null;
                if (n != null && (n.Contains("health-bar") || n.Contains("hud"))) return true;
            }
            return false;
        }

        static bool IsOurs(Transform x)
        {
            for (var p = x; p != null; p = p.parent)
                if (p.name == "GoalMarker" || p.GetComponent<PlaylistTargetCircle>() != null || p.GetComponent<PlaylistLockField>() != null) return true;
            return false;
        }

        // The flattish triangle (pointing down), a soft glow disc behind it, the goal icon above, the number in brand type.
        // Every pin of a group shares the group's two materials (one batch).
        Pin MakePin(Group g)
        {
            if (s_arrow == null) s_arrow = ArrowMesh();
            if (s_arrowBase == null) s_arrowBase = MagicUI.MagicLook.HoloMat(Texture2D.whiteTexture, Color.white, false, MagicUI.MagicLook.QGlow) ?? new Material(Shader.Find("Sprites/Default"));
            if (s_glowBase == null) s_glowBase = MagicUI.MagicLook.HoloMat(MagicUI.MagicLook.GlowDiscTexture(), Color.white, true, MagicUI.MagicLook.QGlass) ?? new Material(Shader.Find("Sprites/Default"));
            if (g.arrow == null) { g.arrow = new Material(s_arrowBase) { name = "GoalMarkerArrow" }; SetColor(g.arrow, g.tint); Exposure(g.arrow, 1.35f); }
            if (g.glow == null) { g.glow = new Material(s_glowBase) { name = "GoalMarkerGlow" }; SetColor(g.glow, new Color(1f, 1f, 1f, 0.42f)); }
            var p = new Pin { go = new GameObject("GoalMarker") };
            p.go.transform.SetParent(transform, false);
            p.go.hideFlags = HideFlags.DontSave;
            p.glow = MagicUI.MagicLook.Plate(p.go.transform, "Glow", MagicUI.MagicLook.Quad(), g.glow, new Vector3(0, 0.01f, 0.01f), Quaternion.identity, Vector3.one * ArrowSize * 1.8f).transform;
            p.arrow = MagicUI.MagicLook.Plate(p.go.transform, "Arrow", s_arrow, g.arrow, Vector3.zero, Quaternion.identity, Vector3.one * ArrowSize).transform;
            var im = IconMat(g.icon);
            if (im != null) p.icon = MagicUI.MagicLook.Plate(p.go.transform, "Icon", MagicUI.MagicLook.Quad(), im, new Vector3(0, ArrowSize * IconY, 0), Quaternion.identity, Vector3.one * ArrowSize * IconS).transform;
            p.label = MagicUI.MagicLook.Label(p.go.transform, "Number", 230f, Color.white, 0.6f, true);
            p.label.transform.localPosition = new Vector3(0, ArrowSize * (im != null ? LabelYIcon : LabelYBare), 0);
            p.label.text = "";
            if (Xray) MagicUI.MagicLook.SetXray(p.go.transform, true);
            // The lowest UI: every HUD / panel (sorting order ≥ 0) draws over the pin (PlaylistGoalSight.MarkerSortingOrder).
            foreach (var r in p.go.GetComponentsInChildren<Renderer>(true)) r.sortingOrder = PlaylistGoalSight.MarkerSortingOrder;
            var rs = new List<Renderer>();
            foreach (var r in p.go.GetComponentsInChildren<Renderer>(true)) if (r.GetComponent<TMPro.TMP_Text>() == null) rs.Add(r);
            p.rends = rs.ToArray();
            return p;
        }

        /// A STILL pin in the approved look (glow + chevron + icon, all white; no number) with none of a live marker's
        /// behaviour (no targets, sight, fade or sizing) — for a miniature (opening-sync, Oct 21: the intro's playlist marker,
        /// PlaylistIntroMarkers). Built at <see cref="ArrowSize"/>; the caller places, sizes and faces it. Its renderers are
        /// all MeshRenderers (the caller may hand them to a diorama backend).
        public static GameObject StaticPin(Transform parent, string icon)
        {
            if (s_arrow == null) s_arrow = ArrowMesh();
            if (s_arrowBase == null) s_arrowBase = MagicUI.MagicLook.HoloMat(Texture2D.whiteTexture, Color.white, false, MagicUI.MagicLook.QGlow) ?? new Material(Shader.Find("Sprites/Default"));
            if (s_glowBase == null) s_glowBase = MagicUI.MagicLook.HoloMat(MagicUI.MagicLook.GlowDiscTexture(), Color.white, true, MagicUI.MagicLook.QGlass) ?? new Material(Shader.Find("Sprites/Default"));
            var arrow = new Material(s_arrowBase) { name = "GoalMarkerArrow" }; SetColor(arrow, Ink); Exposure(arrow, 1.35f);
            var glow = new Material(s_glowBase) { name = "GoalMarkerGlow" }; SetColor(glow, new Color(1f, 1f, 1f, 0.42f));
            var go = new GameObject("GoalMarker") { hideFlags = HideFlags.DontSave };
            if (parent != null) go.transform.SetParent(parent, false);
            MagicUI.MagicLook.Plate(go.transform, "Glow", MagicUI.MagicLook.Quad(), glow, new Vector3(0, 0.01f, 0.01f), Quaternion.identity, Vector3.one * ArrowSize * 1.8f);
            MagicUI.MagicLook.Plate(go.transform, "Arrow", s_arrow, arrow, Vector3.zero, Quaternion.identity, Vector3.one * ArrowSize);
            var im = IconMat(icon);
            if (im != null) MagicUI.MagicLook.Plate(go.transform, "Icon", MagicUI.MagicLook.Quad(), im, new Vector3(0, ArrowSize * IconY, 0), Quaternion.identity, Vector3.one * ArrowSize * IconS);
            return go;
        }

        static void Exposure(Material m, float e) { if (m != null && m.HasProperty("_Exposure")) m.SetFloat("_Exposure", e); }

        static void SetColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        /// A flat downward chevron (Oct 20: squashed vertically): a wide front face with a shallow bevelled back, so it
        /// reads from any side. Width 1, height ArrowH (× ArrowSize).
        static Mesh ArrowMesh()
        {
            var m = new Mesh { name = "GoalArrow" };
            float w = 0.5f, h = ArrowH, d = 0.07f;
            var v = new Vector3[]
            {
                new Vector3(-w, h * 0.5f, 0), new Vector3(w, h * 0.5f, 0), new Vector3(0, -h * 0.5f, 0),             // front
                new Vector3(-w * 0.82f, h * 0.4f, d), new Vector3(w * 0.82f, h * 0.4f, d), new Vector3(0, -h * 0.34f, d), // back (smaller)
            };
            var tris = new int[] { 0, 1, 2, 4, 3, 5, 0, 3, 4, 0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 3, 2, 3, 0 };
            m.vertices = v; m.triangles = tris;
            m.colors = new[] { Color.white, Color.white, Color.white, new Color(0.8f, 0.8f, 0.8f), new Color(0.8f, 0.8f, 0.8f), new Color(0.8f, 0.8f, 0.8f) };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Material IconMat(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            if (s_iconMats.TryGetValue(icon, out var m) && m != null) return m;
            // The eight announce kinds: procedural glyphs in the original pins' style (Aidan, Oct 22: "solid white w/ slightly
            // transparent white … like solid glyphs"). Any other name: a shipped glyph wins, else the procedural set.
            Texture2D tex = AnnounceKinds.Normalize(icon) != null ? KindIcon(icon) : MagicUI.MagicLook.Glyph(icon);
            if (tex == null) tex = IconTexture(icon);
            if (tex == null) tex = IconTexture(icon);
            if (tex == null) return null;
            m = MagicUI.MagicLook.HoloMat(tex, Color.white, true, MagicUI.MagicLook.QSticker);
            if (m != null) { m.name = "GoalIcon_" + icon; Exposure(m, 1.6f); }
            s_iconMats[icon] = m;
            return m;
        }

        // ── the eight announce-kind glyphs (64 px, white on clear): solid strokes + a 25 % white body, like the coin pin ──
        const float Body = 0.25f;
        static float Ring(float u, float v, float cx, float cy, float r, float w = 0.03f) => 1f - SS(w * 0.6f, w * 1.6f, Mathf.Abs(Vector2.Distance(new Vector2(u, v), new Vector2(cx, cy)) - r));
        static float Disc(float u, float v, float cx, float cy, float r) => 1f - SS(r - 0.015f, r + 0.015f, Vector2.Distance(new Vector2(u, v), new Vector2(cx, cy)));
        static float Seg(float u, float v, float ax, float ay, float bx, float by, float w)
        {
            Vector2 p = new Vector2(u, v), a = new Vector2(ax, ay), b = new Vector2(bx, by), ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return 1f - SS(w * 0.5f, w * 0.5f + 0.025f, Vector2.Distance(p, a + ab * t));
        }
        /// A shape's border (solid) and body (25 %) from a signed distance d (< 0 inside, in uv units).
        static float Shape(float d, float w = 0.045f) => Mathf.Max((1f - SS(0f, 0.02f, Mathf.Abs(d + w * 0.5f) - w * 0.5f)), d < 0f ? Body : 0f);

        static Texture2D KindIcon(string kind)
        {
            switch (kind)
            {
                case "collect": return IconTexture("coin");      // the approved coin pin
                case "defeat": return IconTexture("enemy");       // the approved enemy crosshair
                case "waypoint": return KindIcon("reach");         // a place: the flag
                case "target": return MagicUI.MagicLook.Tex("GoalKTarget", 64, 64, (u, v) =>
                {
                    float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f));
                    return Mathf.Max(Ring(u, v, .5f, .5f, .36f), Ring(u, v, .5f, .5f, .2f), Disc(u, v, .5f, .5f, .07f), r < .36f ? Body : 0f);
                });
                case "break": return MagicUI.MagicLook.Tex("GoalKBreak", 64, 64, (u, v) =>
                {
                    float d = Mathf.Max(Mathf.Abs(u - .5f), Mathf.Abs(v - .5f)) - .31f;   // a square block
                    float crack = Mathf.Max(Seg(u, v, .54f, .81f, .44f, .62f, .05f), Mathf.Max(Seg(u, v, .44f, .62f, .57f, .47f, .05f), Seg(u, v, .57f, .47f, .46f, .19f, .05f)));
                    return Mathf.Max(Shape(d), crack);
                });
                case "reach": return MagicUI.MagicLook.Tex("GoalKReach", 64, 64, (u, v) =>
                {
                    bool pole = u > 0.27f && u < 0.33f && v > 0.16f && v < 0.88f;
                    float wave = Mathf.Sin(u * 14f) * 0.02f;
                    float cd = Mathf.Max(Mathf.Max(0.33f - u, u - 0.78f), Mathf.Max(0.52f + wave - v, v - (0.88f + wave)));   // the cloth: solid
                    float ground = (1f - SS(0.9f, 1.05f, Vector2.Distance(new Vector2((u - .3f) / .2f, (v - .14f) / .05f), Vector2.zero))) * Body * 1.4f;
                    return Mathf.Max(pole ? 1f : 0f, 1f - SS(-0.01f, 0.01f, cd), ground);
                });
                case "find": return MagicUI.MagicLook.Tex("GoalKFind", 64, 64, (u, v) =>
                {
                    float r = Vector2.Distance(new Vector2(u, v), new Vector2(.43f, .57f));
                    const float k = 0.7071f;   // the handle starts at the lens rim and never enters the lens
                    float handle = Seg(u, v, .43f + k * .275f, .57f - k * .275f, .8f, .2f, .09f) * SS(.225f, .245f, r);
                    return Mathf.Max(Ring(u, v, .43f, .57f, .23f, .035f), r < .23f ? Body : 0f, handle);
                });
                case "protect": return MagicUI.MagicLook.Tex("GoalKProtect", 64, 64, (u, v) =>
                {
                    float x = Mathf.Abs(u - .5f);
                    float half = v >= .45f ? .27f : .27f * Mathf.Pow(Mathf.Clamp01((v - .14f) / .31f), 0.75f);   // flat top, pointed bottom
                    float d = Mathf.Max(x - half, Mathf.Max(v - .84f, .14f - v));
                    float cross = Mathf.Max(Seg(u, v, .5f, .2f, .5f, .8f, .06f), Seg(u, v, .27f, .6f, .73f, .6f, .06f)) * (1f - SS(-0.02f, 0f, d));   // a cross through it
                    return Mathf.Max(Shape(d), cross);
                });
                case "catch": return MagicUI.MagicLook.Tex("GoalKCatch2", 64, 64, (u, v) =>
                {
                    // an open hand reaching out: palm + four spread fingers + thumb (one outlined shape, soft palm)
                    float Cap(float ax, float ay, float bx, float by, float rr)
                    {
                        Vector2 p = new Vector2(u, v), a = new Vector2(ax, ay), ab = new Vector2(bx, by) - a;
                        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                        return Vector2.Distance(p, a + ab * t) - rr;
                    }
                    float palm = (Vector2.Distance(new Vector2((u - .52f) / .2f, (v - .33f) / .17f), Vector2.zero) - 1f) * .17f;
                    float d = Mathf.Min(palm, Mathf.Min(Mathf.Min(Cap(.38f, .42f, .29f, .73f, .047f), Cap(.47f, .45f, .43f, .83f, .05f)),
                        Mathf.Min(Mathf.Min(Cap(.56f, .45f, .57f, .82f, .049f), Cap(.64f, .42f, .7f, .72f, .044f)), Cap(.36f, .3f, .17f, .47f, .05f))));
                    return Shape(d);
                });
            }
            return IconTexture("enemy");
        }

        /// GLSL smoothstep (Mathf.SmoothStep interpolates between its first two arguments instead).
        static float SS(float e0, float e1, float x) { float t = Mathf.Clamp01((x - e0) / (e1 - e0)); return t * t * (3f - 2f * t); }

        /// Procedural light glyphs for the universal icons (white on clear, additive).
        static Texture2D IconTexture(string icon)
        {
            switch (icon)
            {
                case "coin": return MagicUI.MagicLook.Tex("GoalCoin", 64, 64, (u, v) =>
                {
                    float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f));
                    float ring = 1f - SS(0.02f, 0.05f, Mathf.Abs(r - 0.36f));
                    float bar = Mathf.Abs(u - .5f) < 0.06f && Mathf.Abs(v - .5f) < 0.2f ? 1f : 0f;
                    return Mathf.Max(ring, bar, r < 0.4f ? 0.25f : 0f);
                });
                case "enemy": case "target": return MagicUI.MagicLook.Tex("GoalEnemy", 64, 64, (u, v) =>
                {
                    float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f));
                    float ring = 1f - SS(0.02f, 0.045f, Mathf.Abs(r - 0.3f));
                    float cross = (Mathf.Abs(u - .5f) < 0.035f && Mathf.Abs(v - .5f) > 0.14f && Mathf.Abs(v - .5f) < 0.46f)
                               || (Mathf.Abs(v - .5f) < 0.035f && Mathf.Abs(u - .5f) > 0.14f && Mathf.Abs(u - .5f) < 0.46f) ? 1f : 0f;
                    return Mathf.Max(ring, cross, r < 0.06f ? 1f : 0f);
                });
                case "item": return MagicUI.MagicLook.Tex("GoalItem", 64, 64, (u, v) =>
                {
                    float x = u - .5f, y = v - .5f, a = Mathf.Atan2(y, x), r = Mathf.Sqrt(x * x + y * y);
                    float star = 0.2f + 0.16f * Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 2.5f)), 3f);
                    return 1f - SS(star - 0.02f, star + 0.02f, r);
                });
                case "start": return MagicUI.MagicLook.Tex("GoalStart", 64, 64, (u, v) =>
                {
                    float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f));
                    float ring = 1f - SS(0.02f, 0.045f, Mathf.Abs(r - 0.4f));
                    bool tri = u > 0.38f && u < 0.7f && Mathf.Abs(v - .5f) < (0.7f - u) * 0.62f;
                    return Mathf.Max(ring, tri ? 1f : 0f);
                });
                case "flag": return MagicUI.MagicLook.Tex("GoalFlag", 64, 64, (u, v) =>
                {
                    bool pole = u > 0.26f && u < 0.32f && v > 0.1f && v < 0.9f;
                    bool cloth = u >= 0.32f && u < 0.78f && v > 0.52f + Mathf.Sin(u * 14f) * 0.02f && v < 0.88f + Mathf.Sin(u * 14f) * 0.02f;
                    return pole || cloth ? 1f : 0f;
                });
                case "kick": return MagicUI.MagicLook.Tex("GoalKick", 64, 64, (u, v) =>
                {
                    float r = Vector2.Distance(new Vector2(u, v), new Vector2(.25f, .3f));
                    float arc = (u > 0.3f && v > 0.25f) ? (1f - SS(0.02f, 0.05f, Mathf.Min(Mathf.Abs(r - 0.32f), Mathf.Abs(r - 0.46f)))) : 0f;
                    float ball = Vector2.Distance(new Vector2(u, v), new Vector2(.72f, .7f)) < 0.13f ? 1f : 0f;
                    return Mathf.Max(arc, ball);
                });
            }
            return null;
        }

        void OnDestroy() => Clear();
    }

    /// The purple light lines the movement challenges draw on the floor: a crisp bright purple line with ONE smooth sheet
    /// of soft purple light rising off it (PlaylistGoalLight; goal-visuals Oct 21 — Aidan: "why isn't it one clean
    /// sheet"), and the jump arch: one bright curved line. Pop-in / hide with a little light.
    public static class PlaylistMoveVisuals
    {
        public const float LineW = PlaylistGoalLight.LineCoreW, PopSeconds = 0.35f;

        /// A straight line `len` m long along the root's local X, on the floor: one mesh, one material.
        public static GameObject Line(Transform parent, Vector3 localPos, Quaternion localRot, float len, string name)
        {
            var mat = PlaylistGoalLight.Material(MagicUI.MagicLook.QFloor + 1);
            if (mat == null) return null;
            var root = new GameObject(name) { hideFlags = HideFlags.DontSave }.transform;
            root.SetParent(parent, false);
            root.localPosition = localPos + Vector3.up * 0.01f;
            root.localRotation = localRot;
            var g = new GameObject("Light"); g.transform.SetParent(root, false);
            var mesh = PlaylistGoalLight.LineMesh(len);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            PlaylistGoalLight.Plain(r);
            var glow = root.gameObject.AddComponent<PlaylistGoalGlow>(); glow.mat = mat; glow.mesh = mesh;
            return root.gameObject;
        }

        /// The jump arch a → b (local to `parent`): one bright purple curved line, `rise` m high at its middle, facing
        /// whoever looks at it, soft at its ends.
        public static GameObject Arch(Transform parent, Vector3 a, Vector3 b, string name) => PlaylistGoalArch.Create(parent, a, b, ArchRise(a, b), name);

        /// The arch's height at its middle for a hop a → b (m).
        public static float ArchRise(Vector3 a, Vector3 b)
        {
            float d = Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z));
            return Mathf.Clamp(d * 0.4f, 0.35f, 1.4f);
        }

        /// Human-scale footprints on the floor at `localPos` (local to `parent`), pointing along `dir` (local, horizontal):
        /// a left and a right print, ~27 cm long, in the goal glyph style (solid outline + soft body). `landing` = white
        /// (where you land), else bright purple (where you start). Two quads, one material.
        public const float FootLen = 0.27f, FootWide = 0.11f, FootGap = 0.09f;
        public static GameObject Footprints(Transform parent, Vector3 localPos, Vector3 dir, bool landing, string name)
        {
            var violet = MagicUI.MagicLook.VioletBright;
            Color c = landing ? new Color(1f, 0.97f, 1f, 0.95f) : new Color(violet.r, violet.g, violet.b, 0.95f);
            var mat = MagicUI.MagicLook.HoloMat(FootTexture(), c, true, MagicUI.MagicLook.QFloor + 1);
            if (mat == null) return null;
            if (mat.HasProperty("_Exposure")) mat.SetFloat("_Exposure", landing ? 1.3f : 1.6f);
            var root = new GameObject(name) { hideFlags = HideFlags.DontSave }.transform;
            root.SetParent(parent, false);
            dir.y = 0; if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            root.localPosition = localPos + Vector3.up * 0.012f;
            root.localRotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            for (int side = -1; side <= 1; side += 2)
            {
                // a little toe-out, the left print mirrored
                var q = Quaternion.Euler(90f, side * 6f, 0f);
                var go = MagicUI.MagicLook.Plate(root, side < 0 ? "Left" : "Right", MagicUI.MagicLook.Quad(), mat,
                    new Vector3(side * FootGap, 0f, side < 0 ? 0.02f : -0.02f), q, new Vector3(side * FootWide, FootLen, 1f));
                var r = go != null ? go.GetComponent<Renderer>() : null;
                if (r != null) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false; }
            }
            return root.gameObject;
        }

        static float SS(float e0, float e1, float x) { float t = Mathf.Clamp01((x - e0) / (e1 - e0)); return t * t * (3f - 2f * t); }

        /// A bare right footprint (toes up, 64 × 128): the sole + heel as one rounded shape and four toe dots — solid outline,
        /// 25 % body, like the goal glyphs.
        static Texture2D FootTexture() => MagicUI.MagicLook.Tex("GoalFootprint", 64, 128, (u, v) =>
        {
            float E(float cx, float cy, float rx, float ry) { float dx = (u - cx) / rx, dy = (v - cy) / ry; return (Mathf.Sqrt(dx * dx + dy * dy) - 1f) * Mathf.Min(rx, ry); }
            float d = Mathf.Min(E(.52f, .6f, .36f, .19f), Mathf.Min(E(.46f, .22f, .29f, .16f), E(.5f, .41f, .25f, .2f)));   // ball, heel, arch
            float edge = 1f - SS(0f, 0.02f, Mathf.Abs(d + 0.025f) - 0.025f);
            float body = d < 0f ? 0.42f : 0f;
            float toes = 0f;
            float[,] t = { { .68f, .87f, .13f }, { .45f, .9f, .1f }, { .28f, .86f, .085f }, { .15f, .8f, .07f } };
            for (int i = 0; i < 4; i++) toes = Mathf.Max(toes, 1f - SS(t[i, 2] - .015f, t[i, 2] + .015f, Vector2.Distance(new Vector2(u, v * 0.5f), new Vector2(t[i, 0], t[i, 1] * 0.5f))));
            return Mathf.Max(Mathf.Max(edge, body), toes);
        });

        /// Show / hide with a little light: scale pops (0 → 1.12 → 1) and a small glint burst on the way in.
        public static void Pop(GameObject go, bool on, ref float since, bool burst = true, bool scale = true)
        {
            if (go == null) return;
            if (on && !go.activeSelf)
            {
                go.SetActive(true); since = Time.time;
                if (burst) { var b = MagicUI.MagicLook.Burst(go.transform, 0.35f); if (b != null) { var m = b.main; m.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f); b.Emit(18); } }
            }
            else if (!on && go.activeSelf) go.SetActive(false);
            if (go.activeSelf && scale)
            {
                float k = Mathf.Clamp01((Time.time - since) / PopSeconds);
                float s = k < 0.6f ? Mathf.Lerp(0.2f, 1.12f, k / 0.6f) : Mathf.Lerp(1.12f, 1f, (k - 0.6f) / 0.4f);
                go.transform.localScale = new Vector3(s, 1f, s);
            }
        }
    }

    /// CROSSING — stop.crossing{from='start', to='far', bounds='footprint', fail_on={…}, time=?} on THIS device's wearer
    /// (PlaylistCrossingRules): the bright purple START line shows; past it, the start fades and the FINISH line + its
    /// flag pin pop in; a fail (fail_on: hazard / out / fall / hit{cause} / an event / time) hides the finish and shows
    /// the start again; the far edge bursts. Emits crossing_start / crossing_fail{why, cause, fails} / crossing_done{fails}
    /// for the leader's goal block. (Kept as PlaylistJumpTracker: the editor harness bots drive it by this name.)
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlaylistJumpTracker : MonoBehaviour
    {
        public const float SideMargin = 0.35f;
        public enum Phase { Waiting, Crossing, Crossed }
        public Phase State => _rules == null ? Phase.Waiting : _rules.State == PlaylistCrossingRules.Phase.Crossing ? Phase.Crossing
            : _rules.State == PlaylistCrossingRules.Phase.Done ? Phase.Crossed : Phase.Waiting;
        public int Resets => _rules != null ? _rules.Fails : 0;
        public PlaylistCrossingRules Rules => _rules;
        /// The lines now (checks / screenshots).
        public bool StartShown => _startLine != null && _startLine.activeSelf;
        public bool FinishShown => _finishLine != null && _finishLine.activeSelf;

        PlaylistCrossingRules _rules;
        string _occ, _spec, _resetSeen;
        bool _on;
        float _next, _startPop, _finishPop;
        Vector3 _start, _far;
        GameObject _startLine, _finishLine, _flag;
        string _evName, _evCause;

        public void Spec(PlaylistRun run, string occ, string json, string resetJson)
        {
            _occ = occ;
            _on = true;
            if (json != _spec)
            {
                _spec = json;
                var j = SharedState.ParseFragment(json);
                // fail_on (crossing) — or an old jump spec's reset_on — default {'out', 'hazard'}.
                var fo = j?.GetField("fail_on") ?? j?.GetField("reset_on");
                var failOn = PlaylistFailOn.Parse(fo, new[] { "out", "hazard" });
                double time = j != null ? PlaylistRunPlan.Num(j, "time") : 0;
                if (time > 0 && failOn.time <= 0) failOn.time = time;
                _rules = new PlaylistCrossingRules(failOn);
                Vector3? s = PlaylistGoalSpace.Edge(gameObject, PlaylistRunPlan.Str(j, "from", "start"));
                Vector3? f = PlaylistGoalSpace.Edge(gameObject, PlaylistRunPlan.Str(j, "to", "far"));
                _start = s ?? Vector3.zero; _far = f ?? Vector3.zero;
                Debug.Log($"[pl-runtime] crossing at {occ}: start {_start} → far {_far} (fail on{(failOn.@out ? " out" : "")}{(failOn.hazard ? " hazard" : "")}{(failOn.fall ? " fall" : "")}{(failOn.anyHit ? " hit" : "")}{(failOn.hitCauses.Count > 0 ? " hit:" + string.Join("/", failOn.hitCauses) : "")}{(failOn.events.Count > 0 ? " events:" + string.Join("/", failOn.events) : "")}{(failOn.time > 0 ? " time " + failOn.time : "")})");
                BuildLines();
            }
            // A reset the leader asked for (stop.crossing_reset / stop.jump_reset) for this wearer.
            if (resetJson != null && resetJson != _resetSeen)
            {
                bool first = _resetSeen == null;
                _resetSeen = resetJson;
                var rj = SharedState.ParseFragment(resetJson);
                string who = PlaylistRunPlan.Str(rj, "p", "");
                if (!first && (who == "" || who == PlayerPresence.MyId) && _rules != null)
                {
                    var ev = _rules.Force(PlaylistRunPlan.Str(rj, "why", "forced"));
                    if (ev.HasValue) EmitAll(new List<PlaylistMoveEvent> { ev.Value });
                }
            }
        }

        void OnEnable() => PlaylistEvents.Raised += OnTitleEvent;
        void OnDisable() => PlaylistEvents.Raised -= OnTitleEvent;

        /// A title event on THIS device (Raised carries only this device's events): kept for the next step.
        void OnTitleEvent(PlaylistEvent e)
        {
            if (e == null || e.source != PlaylistEvent.Event || _rules == null || !_rules.FailOn.WatchesEvents) return;
            if (e.name != null && (e.name.StartsWith("crossing_") || e.name.StartsWith("jump_"))) return;
            _evName = e.name; _evCause = PlaylistRunPlan.Str(e.Data, "cause", null);
            _next = 0;   // decide now
        }

        public void Stop()
        {
            _on = false;
            if (_startLine != null) Destroy(_startLine);
            if (_finishLine != null) Destroy(_finishLine);
            if (_flag != null) Destroy(_flag);
        }

        void BuildLines()
        {
            var tpl = GetComponentInChildren<AttractionTemplate>(true);
            if (tpl == null) return;
            Vector2 fp = tpl.RuntimeFootprintMeters;
            Vector3 ax = _far - _start; ax.y = 0;
            bool alongZ = Mathf.Abs(ax.z) >= Mathf.Abs(ax.x);
            float len = (alongZ ? fp.x : fp.y) * 0.9f;
            var rot = Quaternion.Euler(0f, alongZ ? 0f : 90f, 0f);
            if (_startLine == null) _startLine = PlaylistMoveVisuals.Line(tpl.transform, _start, rot, len, "CrossingStart");
            if (_finishLine == null)
            {
                _finishLine = PlaylistMoveVisuals.Line(tpl.transform, _far, rot, len, "CrossingFinish");
                // The finish's small flag pin rides with it (the goal marker look; walls / panels hide it as any marker).
                if (_finishLine != null)
                {
                    _flag = new GameObject("CrossingFlag") { hideFlags = HideFlags.DontSave };
                    _flag.transform.SetParent(_finishLine.transform, false);
                    _flag.AddComponent<PlaylistGoalMarkers>().Spec("crossing", _occ + ".flag", "{\"at\":{\"x\":0,\"y\":0,\"z\":0},\"icon\":\"flag\"}");
                    _finishLine.SetActive(false);
                }
            }
        }

        void Update()
        {
            if (!_on || _rules == null) return;
            PlaylistMoveVisuals.Pop(_startLine, _rules.ShowStart, ref _startPop, burst: false);
            PlaylistMoveVisuals.Pop(_finishLine, _rules.ShowFinish, ref _finishPop);
            if (_rules.State == PlaylistCrossingRules.Phase.Done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            var head = PlaylistObjectiveCallout.Head();
            var tpl = GetComponentInChildren<AttractionTemplate>(true);
            if (head == null || tpl == null) return;
            Vector3 local = tpl.transform.InverseTransformPoint(head.position);
            Vector2 fp = tpl.RuntimeFootprintMeters;
            Vector3 axis = _far - _start; axis.y = 0;
            float len = axis.magnitude;
            if (len < 0.5f) return;
            axis /= len;
            var inp = new PlaylistMoveInput
            {
                now = Time.time, length = len,
                along = Vector3.Dot(new Vector3(local.x, 0, local.z) - _start, axis),
                // inside the footprint's SIDES (lateral to the axis), and not far past either end (the start's approach counts)
                inside = Lateral(local, axis, fp) && Mathf.Abs(local.x) <= fp.x * 0.5f + 1.5f && Mathf.Abs(local.z) <= fp.y * 0.5f + 1.5f,
                hazard = _rules.FailOn.hazard ? OverHazard(head.position, tpl.transform) : null,
                noFloor = _rules.FailOn.fall && NoFloor(head.position, tpl.transform),
                eventName = _evName, eventCause = _evCause, pad = -1,
            };
            _evName = _evCause = null;
            EmitAll(_rules.Step(inp));
        }

        void EmitAll(List<PlaylistMoveEvent> evs)
        {
            foreach (var ev in evs)
            {
                var d = new JSONObject();
                d.AddField("fails", ev.fails);
                if (ev.why != null) d.AddField("why", ev.why);
                if (ev.cause != null) d.AddField("cause", ev.cause);
                PlaylistEvents.Emit(PlaylistEvent.Event, ev.name, d.Print(), _occ);
                Debug.Log($"[pl-runtime] crossing {_occ}: {ev}");
                if (ev.name == "crossing_done" && _finishLine != null)
                {
                    var b = MagicUI.MagicLook.Burst(_finishLine.transform, 0.6f);
                    if (b != null) { b.transform.SetParent(transform, true); b.Play(); Destroy(b.gameObject, 2f); }
                    _finishLine.SetActive(false);
                }
            }
        }

        /// Within the footprint's sides across the start → far axis (+ SideMargin).
        static bool Lateral(Vector3 local, Vector3 axis, Vector2 fp) =>
            Mathf.Abs(axis.z) >= Mathf.Abs(axis.x) ? Mathf.Abs(local.x) <= fp.x * 0.5f + SideMargin : Mathf.Abs(local.z) <= fp.y * 0.5f + SideMargin;

        /// FEET, not head (take 5: a ! block hanging over the lava counted as ground for an upright player and not for a
        /// ducked one): the ground test starts at the wearer's KNEES above the floor and looks down, so nothing above knee
        /// height (a hanging block, a sign, the player's own reach) is ever ground. Knee = the head − 1.1 m, kept between
        /// 0.3 m and 1.2 m above the attraction's floor (a ducked player still tests the floor; one standing on a raised
        /// platform tests the platform).
        public const float KneeBelowHead = 1.1f, KneeMinM = 0.3f, KneeMaxM = 1.2f, GroundRayM = 1.6f;
        public static float GroundRayOriginY(float headY, float floorY) => Mathf.Clamp(headY - KneeBelowHead, floorY + KneeMinM, floorY + KneeMaxM);

        /// A hazard this close under the attraction's level floor is drawn ON the floor, and wins (sal-jumppads, Oct 22: SAL's
        /// lava2 pits lie flush with the platform's play-time LevelFloor — Lava Island 0 cm, Lava Crossing 3 cm under it —
        /// which used to win and made the lava safe). Only the level floor: islands, moving platforms and stepping stones sit
        /// 0–3 cm above their lava and stay ground.
        public const float FlushM = 0.05f;

        /// The platform's generated floor for an attraction (LevelTemplate.GenerateFloorWithHoles: "LevelFloor").
        public static bool IsLevelFloor(Transform t) => t != null && t.name == "LevelFloor";

        /// Pure: what the wearer stands over, from the surfaces under their position (top y, a trigger?, its hazard path
        /// or null, the level floor?): the first one BELOW the ray origin decides — a hazard → its path; a solid surface →
        /// safe (null); triggers that are not hazards are looked through. The level floor with a hazard flush under it
        /// (within FlushM) → that hazard.
        public static string ClassifyGround(IEnumerable<(float top, bool trigger, string hazard, bool levelFloor)> surfaces, float originY)
        {
            var below = new List<(float top, bool trigger, string hazard, bool levelFloor)>();
            foreach (var x in surfaces) if (x.top <= originY) below.Add(x);
            below.Sort((a, b) => b.top.CompareTo(a.top));
            for (int i = 0; i < below.Count; i++)
            {
                var x = below[i];
                if (x.hazard == null && x.trigger) continue;
                if (x.hazard != null) return x.hazard;
                if (x.levelFloor)
                    for (int k = i + 1; k < below.Count && below[k].top >= x.top - FlushM; k++) if (below[k].hazard != null) return below[k].hazard;
                return null;
            }
            return null;
        }

        /// ClassifyGround with no level floor among the surfaces.
        public static string ClassifyGround(IEnumerable<(float top, bool trigger, string hazard)> surfaces, float originY)
        {
            var l = new List<(float top, bool trigger, string hazard, bool levelFloor)>();
            foreach (var x in surfaces) l.Add((x.top, x.trigger, x.hazard, false));
            return ClassifyGround(l, originY);
        }

        /// Is this collider ITSELF a hazard (sal-jumppads, Oct 22: never its parents — islands, moving platforms and the
        /// Lava Maze walkways sit inside L_LAVAPIT_* objects and are ground): tagged Lava, carrying a FloorCutout, or named
        /// *lava* / *hazard* (but not *platform*).
        public static bool OwnHazard(Transform t)
        {
            if (t == null) return false;
            bool tagged = false;
            try { tagged = t.CompareTag("Lava"); } catch (Exception) { }
            if (tagged || t.GetComponent<FloorCutout>() != null) return true;
            string n = t.name.ToLowerInvariant();
            return (n.Contains("lava") || n.Contains("hazard")) && !n.Contains("platform");
        }

        static float FloorY(Transform attraction) => attraction != null ? attraction.position.y : 0f;

        /// No floor under the wearer within reach of the knee ray (a pit, the edge of a platform): only colliders count.
        public static bool NoFloor(Vector3 head, Transform attraction = null)
        {
            float o = GroundRayOriginY(head.y, attraction != null ? FloorY(attraction) : head.y - 1.6f);
            return !Physics.Raycast(new Vector3(head.x, o, head.z), Vector3.down, GroundRayM, ~0, QueryTriggerInteraction.Ignore);
        }

        /// What hazard the wearer stands over: the first thing below their KNEES, judged by the collider that was hit
        /// (OwnHazard: tagged Lava, a floor cutout, named lava / hazard — not its parents), or null on solid ground; a hazard
        /// flush under the level floor wins (ClassifyGround). The attraction's own root never counts ("A_LavaPits").
        public static string OverHazard(Vector3 head, Transform attraction) => OverHazard(head, attraction, null);

        /// OverHazard looking past colliders `skip` says (the jump chain check: a creature or coin standing in the lava).
        public static string OverHazard(Vector3 head, Transform attraction, Func<Collider, bool> skip)
        {
            float o = GroundRayOriginY(head.y, FloorY(attraction));
            var hits = Physics.RaycastAll(new Vector3(head.x, o, head.z), Vector3.down, GroundRayM, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0) return null;
            var surfaces = new List<(float top, bool trigger, string hazard, bool levelFloor)>(hits.Length);
            foreach (var hit in hits)
            {
                var c = hit.collider;
                if (c == null || (attraction != null && !c.transform.IsChildOf(attraction))) continue;
                if (skip != null && skip(c)) continue;
                string hz = c.transform != attraction && OwnHazard(c.transform) ? PlaylistEvents.PathUnder(attraction, c.transform) : null;
                surfaces.Add((hit.point.y, c.isTrigger, hz, IsLevelFloor(c.transform)));
            }
            return ClassifyGround(surfaces, o);
        }
    }

    /// JUMP PADS — stop.jump{pads={posA, posB, …}, window=2.5, fail_on={…}} on THIS device's wearer
    /// (PlaylistJumpPadRules). Aidan, Oct 22: FOOTPRINTS instead of step-in rings —
    ///   • purple footprints on the start side, pointing at the jump;
    ///   • stepping on them: they go, the purple ARCH appears with white landing footprints on the far side, and a little
    ///     chime plays (Resources/MagicUI/GoalStep) so the player knows it is right;
    ///   • standing there ~4.5 s without jumping: Dreamie says "Check to make sure it's safe, then jump!" (a moment with
    ///     data.say, cooldown so she does not repeat it);
    ///   • landing bursts; the landing becomes the next start (its footprints turn toward the next hop), and the next arch
    ///     follows after a short beat; a fail sends the wearer back to the start footprints.
    /// The detection is unchanged (radius PadRadius around each pad). Emits jump_hop{from, to} / jump_fail{why, cause,
    /// fails} / jump_done{fails}.
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlaylistJumpPads : MonoBehaviour
    {
        public const float PadRadius = PlaylistTargetCircle.DefaultRadius;   // the detection radius around each pad
        /// Standing on the start footprints this long reveals the jump (a debounce); right after a landing, a short beat.
        public const float RevealS = 0.25f, AfterLandS = 0.8f;
        /// Start prints sit this far back from the pad centre (away from the gap), landing prints this far past it.
        public const float FootSetback = 0.12f;
        /// Standing on the start this long after the reveal, without jumping: Dreamie's safety nudge (once per stand,
        /// HesitateCooldownS between nudges).
        public const float HesitateS = 4.5f, HesitateCooldownS = 30f;
        public const string HesitateLine = "Check to make sure it's safe, then jump!";
        public PlaylistJumpPadRules Rules => _rules;
        public int StandingIn { get; private set; } = -1;
        /// Footprints / arches shown now (checks / screenshots).
        public int PadsShown { get { int n = 0; foreach (var p in _start) if (p != null && p.activeSelf) n++; foreach (var p in _land) if (p != null && p.activeSelf) n++; return n; } }
        public int StartShown { get { int n = 0; foreach (var p in _start) if (p != null && p.activeSelf) n++; return n; } }
        public int LandingShown { get { int n = 0; foreach (var p in _land) if (p != null && p.activeSelf) n++; return n; } }
        public int ArchesShown { get { int n = 0; foreach (var a in _arches) if (a != null && a.activeSelf) n++; return n; } }
        /// Hops revealed (chimes played) and Dreamie nudges sent (checks).
        public int Reveals { get; private set; }
        public int Nudges { get; private set; }

        PlaylistJumpPadRules _rules;
        float _padR = PadRadius;
        string _occ, _spec;
        bool _on;
        float _next;
        readonly List<Vector3> _local = new List<Vector3>();
        readonly List<GameObject> _start = new List<GameObject>(), _land = new List<GameObject>();
        readonly List<GameObject> _arches = new List<GameObject>();
        float[] _startPop = new float[0], _landPop = new float[0], _archPop = new float[0];
        int _revealed = -1;               // the start pad whose jump is revealed now (-1 = none)
        float _onStartSince = -1f, _landedAt = -10f, _revealedAt = -1f;
        bool _nudged;
        static AudioClip s_step;
        string _evName, _evCause;
        Transform _frame;
        // live pads (sal-jumppads, Oct 22): the authored pads stay in their pit's frame and are re-read every tick, so a pit
        // moved / rotated after Spec carries its footprints and arches along. _host null = explicit pads (frame-local).
        Transform _host;
        readonly List<Vector3> _hostLocal = new List<Vector3>();
        /// The chain this jump runs (explicit or authored + checked as placed); pads 0 + fellBack when none is valid.
        public JumpChain Placed { get; private set; }
        /// Times the visuals were rebuilt because the pads moved with their pit (checks).
        public int Relaid { get; private set; }
        /// stop.jump{draw = false}: the game draws the jump from the cues (jump_ready / jump_armed / jump_landed /
        /// jump_failed / jump_done → the attraction's objects, dp.on); the platform draws nothing (rules, hazard fails, the
        /// chime and Dreamie's hesitation hint unchanged).
        public bool Draw { get; private set; } = true;
        PlaylistJumpCues _cues;
        /// The cues sent so far on this device (checks).
        public readonly List<string> CuesSent = new List<string>();

        public void Spec(PlaylistRun run, string occ, string json)
        {
            _occ = occ; _on = true;
            if (json == _spec) return;
            _spec = json;
            Clear();
            var j = SharedState.ParseFragment(json);
            var explicitPads = new List<Vector3>();
            var pads = j?.GetField("pads");
            if (pads?.list != null)
                foreach (var p in pads.list)
                    if (p != null && p.type == JSONObject.Type.Object)
                        explicitPads.Add(new Vector3((float)PlaylistRunPlan.Num(p, "x"), (float)PlaylistRunPlan.Num(p, "y"), (float)PlaylistRunPlan.Num(p, "z")));
            var failOn = PlaylistFailOn.Parse(j?.GetField("fail_on"), new[] { "hazard" });
            double window = j != null ? PlaylistRunPlan.Num(j, "window") : 0;
            double pr = j != null ? PlaylistRunPlan.Num(j, "radius") : 0;
            var tpl = GetComponentInChildren<AttractionTemplate>(true);
            _frame = tpl != null ? tpl.transform : transform;
            _local.Clear(); _hostLocal.Clear(); _host = null;
            // No explicit pads (≥ 2): the attraction's own authored chain (the source of truth), checked as placed.
            string chain = PlaylistRunPlan.Str(j, "chain", null) ?? PlaylistRunPlan.Str(j, "id", null);
            string source = "explicit";
            if (explicitPads.Count >= 2)
            {
                _local.AddRange(explicitPads); _hostLocal.AddRange(explicitPads);
                Placed = new JumpChain { pads = explicitPads.Count };
            }
            else
            {
                Placed = Resolve(gameObject, chain, out var host, out var hostLocal, out float ar, out float aw);
                if (Placed.pads >= 2)
                {
                    _host = host.transform; _hostLocal.AddRange(hostLocal);
                    foreach (var hp in hostLocal) _local.Add(_frame.InverseTransformPoint(_host.TransformPoint(hp)));
                    source = "authored" + (string.IsNullOrEmpty(chain) ? "" : " chain '" + chain + "'") + " on " + host.name;
                    if (pr <= 0) pr = ar;
                    if (window <= 0) window = aw;
                }
                else
                {
                    source = "none: " + Placed.why;
                    Debug.LogWarning($"[pl-runtime] jump pads at {occ}: no valid chain — {Placed.why}");
                }
            }
            _padR = pr > 0 ? Mathf.Clamp((float)pr, 0.25f, 1f) : PadRadius;
            _rules = new PlaylistJumpPadRules(_local.Count, window, failOn);
            var dr = j?.GetField("draw");
            Draw = dr == null || dr.type != JSONObject.Type.Bool || dr.boolValue;
            BuildVisuals();
            _revealed = -1; _onStartSince = -1f; _landedAt = -10f; _nudged = false;
            _cues = new PlaylistJumpCues(_local.Count);
            Cue(_cues.Begin());
            Debug.Log($"[pl-runtime] jump pads at {occ}: {_local.Count} pad(s) ({source}), window {_rules.Window:0.0} s{(Draw ? "" : ", drawn by the game (draw = false)")}");
        }

        /// Send cues to the attraction's own objects on THIS device (dp.on / onevent), with live world positions.
        void Cue(List<PlaylistJumpCues.Cue> cues)
        {
            if (cues == null || _frame == null) return;
            foreach (var c in cues)
            {
                var d = new JSONObject();
                d.AddField("occurrence", _occ ?? "");
                if (c.name != PlaylistJumpCues.CueDone) d.AddField("hop", c.hop);
                if (c.name == PlaylistJumpCues.CueReady && c.hop >= 0 && c.hop + 1 < _local.Count)
                {
                    Vector3 a = _frame.TransformPoint(_local[c.hop]), b = _frame.TransformPoint(_local[c.hop + 1]);
                    Vector3 dir = b - a; dir.y = 0;
                    float yaw = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir.normalized, Vector3.up).eulerAngles.y : 0f;
                    d.AddField("from", Place(a, yaw)); d.AddField("to", Place(b, yaw));
                    d.AddField("arc_height", PlaylistMoveVisuals.ArchRise(a, b));
                    d.AddField("radius", _padR);
                }
                if (c.name == PlaylistJumpCues.CueArmed || c.name == PlaylistJumpCues.CueLanded || c.name == PlaylistJumpCues.CueFailed)
                    d.AddField("by", PlayerPresence.MyId ?? "");
                if (c.why != null) d.AddField("why", c.why);
                string json = d.Print();
                CuesSent.Add(c.name + " " + c.hop);
                if (CuesSent.Count > 64) CuesSent.RemoveAt(0);
                try { DreamParkLuaPlaylist.DeliverChallengeEvent(gameObject, c.name, json); }
                catch (Exception e) { Debug.LogWarning($"[pl-runtime] jump {_occ}: {c.name} → the attraction threw: {e.Message}"); }
            }
        }

        static JSONObject Place(Vector3 p, float yaw)
        {
            var pos = new JSONObject(); pos.AddField("x", p.x); pos.AddField("y", p.y); pos.AddField("z", p.z);
            var rot = new JSONObject(); rot.AddField("x", 0); rot.AddField("y", yaw); rot.AddField("z", 0);
            var o = new JSONObject(); o.AddField("pos", pos); o.AddField("rot", rot);
            return o;
        }

        /// Footprints + arches for the pads where they are now (Spec, and again when the pit moved them).
        void BuildVisuals()
        {
            Clear();
            for (int i = 0; i < _local.Count; i++)
            {
                // start prints point at the next pad; landing prints along the hop that reaches this pad
                Vector3 outDir = i + 1 < _local.Count ? _local[i + 1] - _local[i] : (i > 0 ? _local[i] - _local[i - 1] : Vector3.forward);
                Vector3 inDir = i > 0 ? _local[i] - _local[i - 1] : outDir;
                // the start prints stand a little back from the jump edge, the landing prints a little past it
                Vector3 oN = outDir; oN.y = 0; oN = oN.sqrMagnitude > 1e-6f ? oN.normalized : Vector3.forward;
                Vector3 iN = inDir; iN.y = 0; iN = iN.sqrMagnitude > 1e-6f ? iN.normalized : Vector3.forward;
                var st = Draw && i + 1 < _local.Count ? PlaylistMoveVisuals.Footprints(_frame, _local[i] - oN * FootSetback, outDir, false, "JumpStart" + i) : null;
                var ld = Draw && i > 0 ? PlaylistMoveVisuals.Footprints(_frame, _local[i] + iN * FootSetback, inDir, true, "JumpLanding" + i) : null;
                if (st != null) st.SetActive(false);
                if (ld != null) ld.SetActive(false);
                _start.Add(st); _land.Add(ld);
                _arches.Add(i == 0 || !Draw ? null : PlaylistMoveVisuals.Arch(_frame, _local[i - 1], _local[i], "JumpArch" + i));
                if (_arches[i] != null) _arches[i].SetActive(false);
            }
            _startPop = new float[_local.Count]; _landPop = new float[_local.Count]; _archPop = new float[_local.Count];
        }

        /// Re-read the authored pads from their pit (it may have moved / turned since Spec); rebuild the visuals if they moved.
        void FollowPit()
        {
            if (_host == null || _frame == null || _hostLocal.Count != _local.Count) return;
            bool moved = false;
            for (int k = 0; k < _local.Count; k++)
            {
                Vector3 l = _frame.InverseTransformPoint(_host.TransformPoint(_hostLocal[k]));
                if ((l - _local[k]).sqrMagnitude > 1e-4f) moved = true;
                _local[k] = l;
            }
            if (!moved) return;
            BuildVisuals(); Relaid++;
            if (_cues != null) Cue(_cues.Moved());
            Debug.Log($"[pl-runtime] jump {_occ}: the pads moved with {_host.name}; footprints follow");
        }

        /// The chain stop.jump{} with no pads runs on this attraction, as PLACED now (sal-jumppads, Oct 22; PLAYLIST-PLAN § 6.7
        /// "works in every frame size and layout"): the authored chain whose pit is switched on in this layout, checked like
        /// SAL's SalJumpPads validator — every pad circle on floor and clear of the hazard, every hop ONE hazard gap of
        /// 0.5–0.9 m with the pads ≤ 0.2 m from its edges, and a 0.9 m flight corridor from the knees to 3 m with no block,
        /// crate, fence or wall in it (creatures and coins don't count). Any failure drops the whole chain: pads 0,
        /// fellBack, why (logged) → stop.jump runs as a crossing. pl-sets returns this from stop.jump{}.
        public static JumpChain Chain(GameObject stopRoot, string chain) => Resolve(stopRoot, chain, out _, out _, out _, out _);

        /// pl-sets' world hook (PlaylistRunWorld.JumpChain, the run op behind stop.jump{}'s return): pads the goal block gives
        /// itself (≥ 2) win as they are; else Chain(root, chain) — the authored chain, checked as placed.
        public static (int pads, bool fellBack, string why) JumpChainHook(PlaylistRunStop stop, GameObject root, string chain, string specJson)
        {
            var spec = SharedState.ParseFragment(string.IsNullOrEmpty(specJson) ? "{}" : specJson);
            int given = spec?.GetField("pads")?.list?.Count ?? 0;
            if (given >= 2) return (given, false, null);
            var c = Chain(root, chain);
            return (c.pads, c.fellBack, c.why);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void HookWorld() { if (PlaylistRunWorld.Main.JumpChain == null) PlaylistRunWorld.Main.JumpChain = JumpChainHook; }

        static JumpChain Resolve(GameObject stopRoot, string chain, out PlaylistJumpPadsAuthoring host, out List<Vector3> hostLocal, out float radius, out float window)
        {
            host = null; hostLocal = new List<Vector3>(); radius = PadRadius; window = (float)PlaylistJumpPadRules.DefaultWindow;
            if (stopRoot == null) return JumpChain.Fell("no attraction");
            var tpl = stopRoot.GetComponentInChildren<AttractionTemplate>(true);
            Transform frame = tpl != null ? tpl.transform : stopRoot.transform;
            var all = new List<PlaylistJumpPadsAuthoring>();
            foreach (var a in stopRoot.GetComponentsInChildren<PlaylistJumpPadsAuthoring>(true))
                if (a != null && a.pads != null && a.pads.Count >= 2) all.Add(a);
            Physics.SyncTransforms();
            var comps = new List<(string chain, bool active, IList<Vector3> pads, float radius)>();
            foreach (var a in all)
            {
                var local = new List<Vector3>();
                foreach (var p in a.pads) local.Add(frame.InverseTransformPoint(a.transform.TransformPoint(p)));
                comps.Add((a.chain, a.enabled && ActiveUnder(a.transform, frame), local, a.radius));
            }
            var skip = Skipper(stopRoot, frame);
            var res = PlaylistJumpChainRules.Evaluate(comps, chain, q => GroundAt(frame, q, skip), (l, k) => Blocker(frame, l[k], l[k + 1], skip), out int i,
                q => RaisedAt(frame, q, skip));
            if (res.fellBack)
            {
                if (i >= 0) res.why = all[i].name + ": " + res.why;
                LogFell(stopRoot, chain, res.why);
                return res;
            }
            host = all[i]; radius = host.radius; window = host.window;
            hostLocal.AddRange(host.pads);
            return res;
        }

        static void LogFell(GameObject stopRoot, string chain, string why) =>
            Debug.LogWarning($"[pl-runtime] jump chain{(string.IsNullOrEmpty(chain) ? "" : " '" + chain + "'")} on {(stopRoot != null ? stopRoot.name : "?")}: dropped, runs as a crossing — {why}");

        /// Switched on in this layout: the object and every parent up to (not incl.) the AttractionTemplate (a frame removes a
        /// pit by switching it off; the attraction itself may still be hidden before its bounce-in).
        static bool ActiveUnder(Transform t, Transform frame)
        {
            for (var c = t; c != null && c != frame; c = c.parent) if (!c.gameObject.activeSelf) return false;
            return true;
        }

        /// What the chain check looks past: creatures and coins — the Enemy layer, and the stop's announced collectibles /
        /// enemies (and anything under them).
        static Func<Collider, bool> Skipper(GameObject stopRoot, Transform frame)
        {
            var live = new HashSet<Transform>();
            try { foreach (var k in new[] { "collect", "defeat" }) foreach (var t in PlaylistAnnounce.Live(stopRoot, k)) if (t != null) live.Add(t); }
            catch (Exception) { }
            return c =>
            {
                if (c == null) return true;
                // announced collectibles / enemies, or anything that looks like a creature, coin or pickup (itself or an ancestor)
                for (var t = c.transform; t != null && t != frame; t = t.parent)
                    if (live.Contains(t) || PlaylistJumpChainRules.LooksCreatureOrPickup(t.name, LayerMask.LayerToName(t.gameObject.layer))) return true;
                return false;
            };
        }

        /// Ground at a frame-local point (pad / hop sample): null = something to stand on, else "hazard <path>" / "no floor".
        static string GroundAt(Transform frame, Vector3 local, Func<Collider, bool> skip)
        {
            Vector3 head = frame.TransformPoint(local) + Vector3.up * 1.6f;
            string hz = PlaylistJumpTracker.OverHazard(head, frame, skip);
            if (hz != null) return "hazard " + hz;
            return PlaylistJumpTracker.NoFloor(head, frame) ? "no floor" : null;
        }

        /// What a pad point stands on when that is NOT the real floor (frame-local point), or null: the top solid under it
        /// (triggers, creatures / coins and overhead things — bottom > OverheadM up — skipped) must lie within RealFloorM of
        /// the attraction's floor (its LevelFloor, the AR floor, a flush painted island).
        public const float OverheadM = 1.0f;
        static string RaisedAt(Transform frame, Vector3 local, Func<Collider, bool> skip)
        {
            float floorY = frame.position.y;
            Vector3 w = frame.TransformPoint(new Vector3(local.x, 0f, local.z));
            var hits = Physics.RaycastAll(new Vector3(w.x, floorY + OverheadM, w.z), Vector3.down, OverheadM + 0.5f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var c = h.collider;
                if (c == null || skip(c)) continue;
                if (c.bounds.min.y - floorY > OverheadM) continue;
                float top = h.point.y - floorY;
                if (Mathf.Abs(top) <= PlaylistJumpChainRules.RealFloorM) return null;
                string where = c.transform.IsChildOf(frame) ? PlaylistEvents.PathUnder(frame, c.transform) : c.name;
                return top > 0 ? $"{where} ({top:0.00} m up)" : $"{where} ({-top:0.00} m below the floor)";
            }
            return null;   // nothing solid: the no-floor test reports it
        }

        /// The first solid in the jumper's corridor a → b (frame-local), or null: 0.9 m wide (body + raised hands), from the
        /// knees to 3 m above the higher pad, pad centre to pad centre + 0.1 m. Ground, platforms and the hazard don't count.
        static string Blocker(Transform frame, Vector3 a, Vector3 b, Func<Collider, bool> skip)
        {
            const float Top = PlaylistJumpChainRules.FlightTop, Half = PlaylistJumpChainRules.CorridorHalf, Knee = PlaylistJumpChainRules.KneeM;
            Vector3 d = b - a; d.y = 0; float len = d.magnitude;
            Vector3 dir = len > 1e-4f ? d / len : Vector3.forward;
            float y0 = Mathf.Max(a.y, b.y);
            Vector3 mid = (a + b) * 0.5f; mid.y = y0 + (Knee + Top) * 0.5f;
            var half = new Vector3(Half, (Top - Knee) * 0.5f, len * 0.5f + 0.1f);
            Vector3 ls = frame.lossyScale; half = new Vector3(half.x * Mathf.Abs(ls.x), half.y * Mathf.Abs(ls.y), half.z * Mathf.Abs(ls.z));
            var rot = frame.rotation * Quaternion.LookRotation(dir, Vector3.up);
            string best = null; float bestY = float.MaxValue;
            foreach (var c in Physics.OverlapBox(frame.TransformPoint(mid), half, rot, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null || !c.transform.IsChildOf(frame) || skip(c)) continue;
                if (PlaylistJumpTracker.OwnHazard(c.transform) || PlaylistJumpTracker.IsLevelFloor(c.transform)) continue;
                if (c.bounds.max.y - frame.position.y < y0 + Knee) continue;   // ground / platforms / pit walls at floor level
                float lo = c.bounds.min.y - frame.position.y;
                if (lo < bestY) { bestY = lo; best = $"{PlaylistEvents.PathUnder(frame, c.transform)} ({lo:0.00}–{c.bounds.max.y - frame.position.y:0.00} m up)"; }
            }
            return best;
        }

        /// The attraction's own authored pads (PlaylistJumpPadsAuthoring under `stopRoot`), local to its AttractionTemplate:
        /// the chain named `chain` (case-insensitive), else the unnamed one, else the first. Empty when none.
        public static List<Vector3> AuthoredPads(GameObject stopRoot, string chain, out float radius, out float window)
        {
            radius = PadRadius; window = (float)PlaylistJumpPadRules.DefaultWindow;
            var l = new List<Vector3>();
            if (stopRoot == null) return l;
            var tpl = stopRoot.GetComponentInChildren<AttractionTemplate>(true);
            Transform frame = tpl != null ? tpl.transform : stopRoot.transform;
            PlaylistJumpPadsAuthoring pick = null, unnamed = null, first = null;
            foreach (var a in stopRoot.GetComponentsInChildren<PlaylistJumpPadsAuthoring>(true))
            {
                if (a == null || a.pads == null || a.pads.Count < 2 || !a.enabled || !ActiveUnder(a.transform, frame)) continue;   // a switched-off pit: no jump
                if (first == null) first = a;
                if (string.IsNullOrEmpty(a.chain) && unnamed == null) unnamed = a;
                if (!string.IsNullOrEmpty(chain) && string.Equals(a.chain, chain, StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
            }
            if (pick == null && string.IsNullOrEmpty(chain)) pick = unnamed != null ? unnamed : first;
            if (pick == null) return l;
            foreach (var p in pick.pads) l.Add(frame.InverseTransformPoint(pick.transform.TransformPoint(p)));
            radius = pick.radius; window = pick.window;
            return l;
        }

        /// How many pads the chain has AS PLACED (Chain: switched-on pit, checked; 0 when dropped). The goal runtime:
        /// stop.jump{} with no pads stays a jump when ≥ 2, else runs as a crossing.
        public static int AuthoredCount(GameObject stopRoot, string chain) => Chain(stopRoot, chain).pads;

        void OnEnable() => PlaylistEvents.Raised += OnTitleEvent;
        void OnDisable() => PlaylistEvents.Raised -= OnTitleEvent;
        void OnTitleEvent(PlaylistEvent e)
        {
            if (e == null || e.source != PlaylistEvent.Event || _rules == null || !_rules.FailOn.WatchesEvents) return;
            if (e.name != null && (e.name.StartsWith("crossing_") || e.name.StartsWith("jump_"))) return;
            _evName = e.name; _evCause = PlaylistRunPlan.Str(e.Data, "cause", null);
            _next = 0;
        }

        void Clear()
        {
            foreach (var p in _start) if (p != null) Destroy(p);
            foreach (var p in _land) if (p != null) Destroy(p);
            foreach (var a in _arches) if (a != null) Destroy(a);
            _start.Clear(); _land.Clear(); _arches.Clear();
        }

        /// What shows for hop `s` → `s+1` now: the start prints (until stepped on), else the arch + landing prints.
        void Visibility()
        {
            bool done = _rules.Done;
            int s = Math.Max(0, _rules.Reached);
            for (int k = 0; k < _local.Count; k++)
            {
                bool showStart = !done && k == s && _revealed != s && !_rules.Airborne;
                bool hopTo = !done && k == s + 1 && (_revealed == s || _rules.Airborne);
                if (_start[k] != null) PlaylistMoveVisuals.Pop(_start[k], showStart, ref _startPop[k]);
                if (_land[k] != null) PlaylistMoveVisuals.Pop(_land[k], hopTo, ref _landPop[k]);
                if (_arches[k] != null) PlaylistMoveVisuals.Pop(_arches[k], hopTo, ref _archPop[k], burst: false, scale: false);
            }
        }

        /// Stepping on the start prints reveals the jump (chime); lingering there nudges Dreamie.
        void StartPad(float now)
        {
            int s = Math.Max(0, _rules.Reached);
            bool onStart = !_rules.Done && StandingIn == s && s + 1 < _local.Count;
            if (!onStart)
            {
                _onStartSince = -1f; _nudged = false;
                // walked off the start without jumping (not airborne): the start prints come back
                if (!_rules.Airborne && _revealed == s && StandingIn != s) _revealed = -1;
                return;
            }
            if (_onStartSince < 0f) _onStartSince = now;
            if (_revealed != s)
            {
                float wait = now - _landedAt < AfterLandS ? AfterLandS - (now - _landedAt) : RevealS;
                if (now - _onStartSince < wait) return;
                _revealed = s; _revealedAt = now; Reveals++;
                PlayStep(_frame.TransformPoint(_local[s]));
                if (_cues != null) Cue(_cues.Armed(s));
                Debug.Log($"[pl-runtime] jump {_occ}: footprints {s} stepped on → arch {s}→{s + 1}");
                return;
            }
            if (!_nudged && now - _revealedAt >= HesitateS)
            {
                _nudged = true; Nudges++;
                var d = new JSONObject();
                d.AddField("say", HesitateLine);
                d.AddField("from", s); d.AddField("to", s + 1);
                DreamieMoments.Moment("jump_hesitate", d.Print(), priority: DreamieMoment.Normal, point: _frame.TransformPoint(_local[s + 1]),
                    cooldown: HesitateCooldownS, stop: _occ);
                Debug.Log($"[pl-runtime] jump {_occ}: hesitating on {s} → Dreamie: {HesitateLine}");
            }
        }

        static void PlayStep(Vector3 at)
        {
            if (s_step == null) { try { s_step = Resources.Load<AudioClip>("MagicUI/GoalStep"); } catch (Exception) { } }
            if (s_step != null) AudioSource.PlayClipAtPoint(s_step, at + Vector3.up * 1.0f, 0.8f);
        }

        public void Stop() { _on = false; Clear(); }
        void OnDestroy() => Clear();

        void Update()
        {
            if (!_on || _rules == null || _local.Count == 0) return;
            FollowPit();
            Visibility();
            if (_rules.Done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.05f;
            var head = PlaylistObjectiveCallout.Head();
            if (head == null || _frame == null) return;
            StandingIn = -1;
            for (int k = 0; k < _local.Count; k++)
            {
                Vector3 c = _frame.TransformPoint(_local[k]);
                Vector2 d = new Vector2(head.position.x - c.x, head.position.z - c.z);
                if (d.magnitude <= _padR) { StandingIn = k; break; }
            }
            var tpl = GetComponentInChildren<AttractionTemplate>(true);
            Vector3 l = _frame.InverseTransformPoint(head.position);
            Vector2 fp = tpl != null ? tpl.RuntimeFootprintMeters : new Vector2(99, 99);
            var inp = new PlaylistMoveInput
            {
                now = Time.time, pad = StandingIn,
                inside = Mathf.Abs(l.x) <= fp.x * 0.5f + PlaylistJumpTracker.SideMargin && Mathf.Abs(l.z) <= fp.y * 0.5f + PlaylistJumpTracker.SideMargin,
                hazard = _rules.FailOn.hazard && StandingIn < 0 ? PlaylistJumpTracker.OverHazard(head.position, _frame) : null,
                noFloor = _rules.FailOn.fall && StandingIn < 0 && PlaylistJumpTracker.NoFloor(head.position, _frame),
                eventName = _evName, eventCause = _evCause,
            };
            _evName = _evCause = null;
            var stepped = _rules.Step(inp);
            StartPad(Time.time);
            foreach (var ev in stepped)
            {
                var d = new JSONObject();
                d.AddField("fails", ev.fails);
                if (ev.name == "jump_hop") { d.AddField("from", ev.from); d.AddField("to", ev.to); }
                if (ev.why != null) d.AddField("why", ev.why);
                if (ev.cause != null) d.AddField("cause", ev.cause);
                PlaylistEvents.Emit(PlaylistEvent.Event, ev.name, d.Print(), _occ);
                Debug.Log($"[pl-runtime] jump {_occ}: {ev}");
                // Landing: a burst on the pad reached; it becomes the next start (revealed after a short beat).
                if ((ev.name == "jump_hop" || ev.name == "jump_done") && ev.to >= 0 && ev.to < _local.Count)
                {
                    _landedAt = Time.time; _revealed = -1; _onStartSince = -1f; _nudged = false;
                    var host = _land[ev.to] != null ? _land[ev.to].transform : _frame;
                    var b = Draw ? MagicUI.MagicLook.Burst(host, _padR) : null;
                    if (b != null) { b.transform.SetParent(transform, true); b.transform.position = _frame.TransformPoint(_local[ev.to]); b.Play(); Destroy(b.gameObject, 2f); }
                }
                if (ev.name == "jump_fail") { _revealed = -1; _onStartSince = -1f; _nudged = false; }
                if (_cues != null) Cue(_cues.Rule(ev, _rules.Reached));
            }
        }
    }

    /// The jump's cues for a game that draws it (Aidan, Oct 22: "made part of SAL … graphically match SAL";
    /// stop.jump{draw = false}). Pure: which cue, for which hop; the device adds live positions and the player.
    ///   jump_ready {hop, from, to, arc_height} — hop `hop` (pad hop → hop+1) is the one to make now (start, after a
    ///     landing, after a fail, and again when the pit moved);
    ///   jump_armed {hop, by} — the player stepped on its start footprints (the arch shows, the start prints go);
    ///   jump_landed {hop, by} · jump_failed {hop, by, why} · jump_done.
    public sealed class PlaylistJumpCues
    {
        public const string CueReady = "jump_ready", CueArmed = "jump_armed", CueLanded = "jump_landed", CueFailed = "jump_failed", CueDone = "jump_done";
        public struct Cue { public string name; public int hop; public string why; public override string ToString() => name + " " + hop + (why != null ? " " + why : ""); }
        public readonly int Count;
        /// The hop to make now (-1 = none: done or no chain).
        public int Hop { get; private set; } = -1;
        public bool IsDone { get; private set; }

        public PlaylistJumpCues(int padCount) { Count = padCount; }

        static List<Cue> One(string n, int hop, string why = null) => new List<Cue> { new Cue { name = n, hop = hop, why = why } };

        public List<Cue> Begin()
        {
            if (Count < 2) return new List<Cue>();
            Hop = 0; IsDone = false;
            return One(CueReady, 0);
        }

        /// The player stepped on hop `hop`'s start footprints.
        public List<Cue> Armed(int hop) => IsDone || hop < 0 ? new List<Cue>() : One(CueArmed, hop);

        /// A rule event (PlaylistJumpPadRules.Step) → cues; `reached` = the rules' Reached after it.
        public List<Cue> Rule(PlaylistMoveEvent ev, int reached)
        {
            var l = new List<Cue>();
            if (IsDone) return l;
            switch (ev.name)
            {
                case "jump_hop":
                    l.Add(new Cue { name = CueLanded, hop = ev.to - 1 });
                    if (ev.to + 1 < Count) { Hop = ev.to; l.Add(new Cue { name = CueReady, hop = Hop }); }
                    break;
                case "jump_done":   // the rules send it right after the last jump_hop (already a landed cue)
                    l.Add(new Cue { name = CueDone, hop = -1 });
                    Hop = -1; IsDone = true;
                    break;
                case "jump_fail":
                    l.Add(new Cue { name = CueFailed, hop = Math.Max(0, Hop), why = ev.why });
                    Hop = Math.Max(0, reached);
                    l.Add(new Cue { name = CueReady, hop = Hop });
                    break;
            }
            return l;
        }

        /// The pads moved with their pit: the hop to make now, again (new positions).
        public List<Cue> Moved() => IsDone || Hop < 0 ? new List<Cue>() : One(CueReady, Hop);
    }

    /// What stop.jump{} runs on an attraction (PlaylistJumpPads.Chain): `pads` ≥ 2 = a jump over that many pads; 0 +
    /// `fellBack` = no valid chain as placed (`why`), so it runs as a crossing. pl-sets returns it from stop.jump{}.
    public struct JumpChain
    {
        public int pads;
        public bool fellBack;
        public string why;
        public static JumpChain Fell(string why) => new JumpChain { pads = 0, fellBack = true, why = why };
        public string ToJson()
        {
            var o = new JSONObject();
            o.AddField("pads", pads); o.AddField("fellBack", fellBack);
            if (!string.IsNullOrEmpty(why)) o.AddField("why", why);
            return o.Print();
        }
        public override string ToString() => fellBack ? $"crossing ({why})" : $"jump, {pads} pads";
    }

    /// Pure rules of the jump chain check (sal-jumppads' SalJumpPads validator, run on the chain as placed). Positions are
    /// local to the AttractionTemplate; `ground(p)` = null where there is ground to stand on, else what is there
    /// ("hazard …", "no floor"); `blocker(i)` = the first solid in hop i's flight corridor, or null.
    public static class PlaylistJumpChainRules
    {
        public const float GapMin = 0.5f, GapMax = 0.9f, EdgeMax = 0.2f, PadClear = 0.03f, Tolerance = 0.005f, StepM = 0.01f;
        public const float FlightTop = 3.0f, CorridorHalf = 0.45f, KneeM = 0.25f;

        /// Which authored chain runs (index into comps, each with ≥ 2 pads): the one named `chain` (case-insensitive), else
        /// (no name asked) the unnamed one, else the first — only among chains whose pit is switched on. -1 + why when none.
        public static int Pick(IList<(string chain, bool active)> comps, string chain, out string why)
        {
            why = null;
            bool named = !string.IsNullOrEmpty(chain), anyMatch = false;
            int unnamed = -1, first = -1;
            for (int i = 0; i < comps.Count; i++)
            {
                bool match = named && string.Equals(comps[i].chain ?? "", chain, StringComparison.OrdinalIgnoreCase);
                if (match) anyMatch = true;
                if (!comps[i].active) continue;
                if (match) return i;
                if (first < 0) first = i;
                if (string.IsNullOrEmpty(comps[i].chain) && unnamed < 0) unnamed = i;
            }
            if (named) { why = anyMatch ? $"chain '{chain}': its pit is switched off in this layout" : $"no chain '{chain}' authored"; return -1; }
            if (unnamed >= 0) return unnamed;
            if (first >= 0) return first;
            why = comps.Count > 0 ? "its pit is switched off in this layout" : "no jump pads authored";
            return -1;
        }

        /// A creature, coin or pickup by its name or layer — never in a jump's way, announced or not (a creature moves, a
        /// coin is collected; sal-jumppads, Oct 23: an unannounced E_COIN dropped a valid chain). SAL's E_* roots, *coin* /
        /// *pickup* (not *block*: a ! block holding coins is solid), creature / enemy / pickup layers.
        public static bool LooksCreatureOrPickup(string name, string layer)
        {
            string n = (name ?? "").ToLowerInvariant(), l = (layer ?? "").ToLowerInvariant();
            if (n.StartsWith("e_")) return true;
            if ((n.Contains("coin") || n.Contains("pickup")) && !n.Contains("block")) return true;
            return l == "enemy" || l == "enemies" || l == "creature" || l == "creatures" || l == "pickup" || l == "pickups" || l == "collectible" || l == "collectibles";
        }

        /// Pick + Validate: the chain stop.jump{} runs as placed (`picked` = its index, -1 when none is switched on).
        public static JumpChain Evaluate(IList<(string chain, bool active, IList<Vector3> pads, float radius)> comps, string chain,
            Func<Vector3, string> ground, Func<IList<Vector3>, int, string> blocker, out int picked, Func<Vector3, string> raised = null)
        {
            var pick = new List<(string chain, bool active)>();
            foreach (var c in comps) pick.Add((c.chain, c.active));
            picked = Pick(pick, chain, out string why);
            if (picked < 0) return JumpChain.Fell(why);
            string Check(int i)
            {
                var pd = comps[i].pads;
                return Validate(pd, comps[i].radius, ground, blocker == null ? (Func<int, string>)null : k => blocker(pd, k), raised);
            }
            if (string.IsNullOrEmpty(chain))
            {
                // no chain named (goal blocks don't know the room): the LONGEST switched-on chain that passes as placed
                // (sal-jumppads, Oct 23: Lava Island's 3-hop 'large' chain in a large room); ties → the default chain first
                int best = -1;
                string pickedBad = null, firstBad = null;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (!comps[i].active || comps[i].pads == null || comps[i].pads.Count < 2) continue;
                    string bad = Check(i);
                    if (bad != null) { if (i == picked) pickedBad = bad; firstBad ??= bad; continue; }
                    if (best < 0 || comps[i].pads.Count > comps[best].pads.Count
                        || (comps[i].pads.Count == comps[best].pads.Count && i == picked)) best = i;
                }
                if (best < 0) return JumpChain.Fell(pickedBad ?? firstBad ?? "no valid chain");
                picked = best;
                return new JumpChain { pads = comps[best].pads.Count };
            }
            string badNamed = Check(picked);
            return badNamed != null ? JumpChain.Fell(badNamed) : new JumpChain { pads = comps[picked].pads.Count };
        }

        /// The whole chain: null when every pad and hop passes, else the first problem ("pad 1: …", "hop 0→1: …").
        public static string Validate(IList<Vector3> pads, float r, Func<Vector3, string> ground, Func<int, string> blocker, Func<Vector3, string> raised = null)
        {
            if (pads == null || pads.Count < 2) return "fewer than 2 pads";
            for (int i = 0; i < pads.Count; i++)
            {
                string p = CheckPad(pads[i], r, ground, raised);
                if (p != null) return $"pad {i}: {p}";
            }
            for (int i = 0; i + 1 < pads.Count; i++)
            {
                string h = CheckHop(pads[i], pads[i + 1], r, ground, out _);
                if (h != null) return $"hop {i}→{i + 1}: {h}";
                string b = blocker?.Invoke(i);
                if (b != null) return $"hop {i}→{i + 1}: flight path blocked by {b}";
            }
            return null;
        }

        /// A pad stands on the REAL floor (Aidan, Oct 22: "a human cannot stand on a virtual block"): the surface under it
        /// within this of the attraction's floor (a flush painted island is fine; a block, rock or raised platform is not).
        public const float RealFloorM = 0.02f;

        /// A pad: its centre and two rings (radius r + PadClear) on ground, clear of the hazard; and (raised, when given)
        /// every point on the real floor — raised(p) = null there, else what it stands on instead.
        public static string CheckPad(Vector3 p, float r, Func<Vector3, string> ground, Func<Vector3, string> raised = null)
        {
            float R = r + PadClear;
            string at(float x, float z)
            {
                var q = new Vector3(x, p.y, z);
                string g = ground(q);
                if (g != null) return $"circle over {g} at ({x:0.00}, {z:0.00})";
                string up = raised?.Invoke(q);
                return up != null ? $"not on the real floor: {up} at ({x:0.00}, {z:0.00})" : null;
            }
            string c = at(p.x, p.z); if (c != null) return c;
            foreach (float rr in new[] { R * 0.5f, R })
                for (int k = 0; k < 24; k++)
                {
                    float a = k * Mathf.PI * 2f / 24f;
                    c = at(p.x + Mathf.Cos(a) * rr, p.z + Mathf.Sin(a) * rr);
                    if (c != null) return c;
                }
            return null;
        }

        /// A hop a → b: ground, ONE hazard span of GapMin–GapMax, ground; both pads ≤ EdgeMax from the gap's edges.
        public static string CheckHop(Vector3 a, Vector3 b, float r, Func<Vector3, string> ground, out float gap)
        {
            gap = 0f;
            var A = new Vector2(a.x, a.z); var B = new Vector2(b.x, b.z);
            float len = Vector2.Distance(A, B);
            int n = Mathf.Max(2, Mathf.CeilToInt(len / StepM));
            int runs = 0; bool inHz = false; float hs = -1f, he = -1f;
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n, u = len * t;
                var q = Vector2.Lerp(A, B, t);
                bool hz = ground(new Vector3(q.x, Mathf.Lerp(a.y, b.y, t), q.y)) != null;
                if (hz && !inHz) { runs++; inHz = true; if (runs == 1) hs = u; }
                if (!hz && inHz) { inHz = false; if (runs == 1) he = u; }
            }
            if (inHz) return "the landing side is hazard";
            if (runs == 0) return "no gap: the hop crosses no hazard";
            if (runs > 1) return $"{runs} hazard spans on the line";
            gap = he - hs;
            float takeoff = hs - r, landing = len - he - r;
            if (gap < GapMin - Tolerance || gap > GapMax + Tolerance) return $"gap {gap:0.00} m (want {GapMin}–{GapMax})";
            if (takeoff > EdgeMax) return $"take-off pad {takeoff:0.00} m back from the gap edge (≤ {EdgeMax})";
            if (landing > EdgeMax) return $"landing pad {landing:0.00} m past the gap edge (≤ {EdgeMax})";
            return null;
        }
    }

    /// REACH — stop.reach{at, radius}: the destination is the target circle (the stop's markers draw it); THIS device's
    /// wearer stepping into it emits reach_done once (and the circle bursts). No crossing lines.
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlaylistReachTracker : MonoBehaviour
    {
        string _occ, _spec;
        Vector3 _at;
        float _radius = PlaylistTargetCircle.DefaultRadius, _next;
        bool _on, _reached;
        public bool Reached => _reached;

        public void Spec(string occ, string json)
        {
            _occ = occ; _on = true;
            if (json == _spec) return;
            _spec = json; _reached = false;
            var j = SharedState.ParseFragment(json);
            var at = j?.GetField("at");
            _at = at != null && at.type == JSONObject.Type.Object ? new Vector3((float)PlaylistRunPlan.Num(at, "x"), (float)PlaylistRunPlan.Num(at, "y"), (float)PlaylistRunPlan.Num(at, "z")) : Vector3.zero;
            double r = j != null ? PlaylistRunPlan.Num(j, "radius") : 0;
            _radius = r > 0 ? (float)r : PlaylistTargetCircle.DefaultRadius;
            Debug.Log($"[pl-runtime] reach at {occ}: {_at} r {_radius:0.0}");
        }

        public void Stop() => _on = false;

        void Update()
        {
            if (!_on || _reached || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            var head = PlaylistObjectiveCallout.Head();
            if (head == null) return;
            var tpl = GetComponentInChildren<AttractionTemplate>(true);
            Vector3 c = (tpl != null ? tpl.transform : transform).TransformPoint(_at);
            if (new Vector2(head.position.x - c.x, head.position.z - c.z).magnitude > _radius) return;
            _reached = true;
            PlaylistEvents.Emit(PlaylistEvent.Event, "reach_done", "{}", _occ);
            Debug.Log($"[pl-runtime] reach {_occ}: reached");
            var gm = GetComponent<PlaylistGoalMarkers>();
            if (gm != null)
                foreach (var circle in gm.Circles)
                    if (circle != null && circle.Contains(head.position, 0.3f)) circle.Reach();
        }
    }

    /// Which visuals a tick still showed, and which it did not (they are torn down). Pure bookkeeping (offline-testable).
    public sealed class PlaylistVisualSweep<T> where T : class
    {
        readonly HashSet<T> _managed = new HashSet<T>(), _seen = new HashSet<T>();
        readonly Func<T, bool> _dead;
        public PlaylistVisualSweep(Func<T, bool> dead = null) { _dead = dead; }
        public int Count => _managed.Count;
        public void Begin() => _seen.Clear();
        public void Seen(T c) { if (c == null) return; _seen.Add(c); _managed.Add(c); }
        /// The managed items this tick did not see (live ones only); they leave the set.
        public List<T> End()
        {
            var gone = new List<T>(); var drop = new List<T>();
            foreach (var c in _managed)
            {
                if (c == null || (_dead != null && _dead(c))) { drop.Add(c); continue; }
                if (_seen.Contains(c)) continue;
                gone.Add(c); drop.Add(c);
            }
            foreach (var c in drop) _managed.Remove(c);
            return gone;
        }
    }
}
