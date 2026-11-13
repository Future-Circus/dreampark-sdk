// ─────────────────────────────────────────────────────────────────────
//  RunnerHost.cs — the Unity side of a WAY TO PLAY on this device (ways-to-play
//  contract §2 / §4): ticks the RunnerDirector, puts level prefabs into their
//  tile frames under the board root (the Location root: origin = the play
//  rect's centre, +Z into the park), dresses tile edges through
//  IRunnerEdgeSocket, reports where this device's player stands, and shows
//  the Next-up / vote panels through pluggable hooks (core: GuestCard).
//
//  Shared with the SDK: the Park Simulator runs any runner with it
//  (Configure + StartSession). Core wires nowPlaying / the session clock /
//  the panels from Assets/Scripts/Runners/RunnerCoreBridge.cs.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace DreamPark
{
    /// Marks a placed level root (dp.level.complete finds its load from any object under it).
    public sealed class RunnerLevelRoot : MonoBehaviour
    {
        public int loadId;
        public string level;
        public int[] tiles = new int[0];
        public float yawDeg;
        [NonSerialized] public RunnerDirector director;
        public bool ready;
    }

    public sealed class RunnerHost : MonoBehaviour
    {
        public static RunnerHost Instance { get; private set; }

        /// address → prefab. Null = Addressables (CoreExtensions.GetAsset). Return null for "not available" (a
        /// placeholder stands in when Placeholders is on).
        public static Func<string, Task<GameObject>> LevelProvider;
        /// Panels (null argument = hide). Core: GuestCard (RunnerCoreBridge). Default: a plain floating text panel.
        public static Action<RunnerDirector, RunnerNextUp> NextUpPanel;
        public static Action<RunnerDirector, RunnerVote> VotePanel;
        /// The play session's clock (seconds left, -1 unknown) and length in minutes.
        public static Func<double> SessionTimeLeft = () => -1;
        public static Func<double> SessionMinutes = () => 0;
        /// Seconds until the shared clock starts (the pile-in; > 0 counting, 0 started, -1 unknown = started).
        public static Func<double> SessionStartsIn = () => -1;
        /// A player's display name for dp.zones.players() (null = the id).
        public static Func<string, string> PlayerName;
        /// This device's player position in world space (null = Camera.main).
        public static Func<Vector3?> PlayerWorldPos;
        /// More players for presence / votes (world space): the Park Simulator's fake players. Remote players
        /// (RemotePlayer: real headsets, dreampark_peers) are added on their own.
        public static Func<List<(string id, Vector3 world)>> ExtraPlayers;
        /// Draw a labelled box for a level whose prefab is not available (editor / simulator / tests).
        public static bool Placeholders = Application.isEditor;
        /// Draw the tile grid on the floor (editor / simulator / debugging).
        public static bool ShowBoard = Application.isEditor;
        public static Action<string> Log = m => Debug.Log(m);
        /// The title's own services for a board (core: the Player rig + Game Manager through DreamParkPackageRuntime;
        /// the Park Simulator: its own). Called once per Run (fire and forget) and OnStop on Stop.
        public static Action<RunnerHost> OnRun;
        public static Action<RunnerHost, string> OnStop;
        /// A level root this device placed (the instance is under it), e.g. to link it to the title's package.
        /// Called in the same frame the level was instantiated, before it renders (core: its spawn transition holds it here).
        public static Action<RunnerLevelRoot> OnLevelPlaced;
        /// A level root about to be destroyed (the director swapped it out), called right BEFORE the Destroy so a
        /// transition can snapshot it (core: AttractionTransition.PlayOut). Not called when the whole board stops.
        public static Action<RunnerLevelRoot> OnLevelRemoving;
        /// A level the floor vote hid comes back (core: its spawn transition). Levels PLACED while the vote showed get
        /// OnLevelPlaced instead, at the moment they first appear.
        public static Action<RunnerLevelRoot> OnLevelReshown;
        /// Big level prefabs cloned off the main thread (core: LevelAnchor's staged spawn, the Oct 11 load-hitch fix):
        /// returns an INACTIVE clone (or null = plain Instantiate) that waited for this frame's activation budget.
        public static Func<GameObject, Task<GameObject>> StagedClone;
        /// Wakes a staged clone under the holder (Awake/OnEnable run here, where Instantiate ran them; local pose kept).
        /// Null = a plain SetParent.
        public static Func<GameObject, Transform, GameObject> ActivateClone;

        public RunnerDirector Director { get; private set; }
        public Transform BoardRoot { get; private set; }
        public string ContentId => Director?.ContentId;
        public string RunnerId => Director?.Runner?.runnerId;

        readonly Dictionary<int, RunnerLevelRoot> _roots = new Dictionary<int, RunnerLevelRoot>();
        readonly Dictionary<string, (string look, bool locked)> _edgeLooks = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        readonly Dictionary<string, GameObject> _links = new Dictionary<string, GameObject>(StringComparer.Ordinal);   // "t:side" → spawned link
        GameObject _grid;
        TextMeshPro _panel;
        string _panelText;
        RunnerNextUp _nu; RunnerVote _vote;

        public static RunnerHost Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("DreamPark Runner Host");
            if (Application.isPlaying) DontDestroyOnLoad(go);
            Instance = go.AddComponent<RunnerHost>();
            return Instance;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Wire(RunnerWorld.Main);
            RunnerLua.LevelComplete = LevelCompleteFrom;
            RunnerLua.CurrentDirector = () => Instance != null ? Instance.Director : null;
            RunnerLua.LoadIdOf = go => { var r = go != null ? go.GetComponentInParent<RunnerLevelRoot>() : null; return r != null ? r.loadId : -1; };
            RunnerLua.BoardToWorld = (x, z) => Instance != null && Instance.BoardRoot != null ? Instance.BoardRoot.TransformPoint(new Vector3(x, 0, z)) : new Vector3(x, 0, z);
        }

        void OnDestroy()
        {
            if (Instance == this) { Stop("host destroyed"); Instance = null; }
        }

        void Wire(RunnerWorld w)
        {
            w.Log = m => Log?.Invoke(m);
            w.LocalPos = d => LocalPos();
            w.RemotePositions = d => RemotePositions();
            w.Place = (d, l, lv) => Place(d, l, lv);
            w.Remove = (d, l) => RemoveLoad(l);
            w.Edge = (d, tile, side, look, locked) => { _edgeLooks[tile + ":" + side] = (look, locked); ApplyEdges(); };
            w.Link = (d, tile, side, link, on) => { if (on) SpawnLink(d, tile, side, link); else RemoveLink(tile + ":" + side); };
            w.NextUp = (d, n) => { _nu = n; if (NextUpPanel != null) NextUpPanel(d, n); else Fallback(); };
            w.Vote = (d, v) => { _vote = v; DrawVoteZones(d, v); if (VotePanel != null) VotePanel(d, v); else Fallback(); };
            w.SessionTimeLeft = () => SessionTimeLeft?.Invoke() ?? -1;
            w.SessionMinutes = () => SessionMinutes?.Invoke() ?? 0;
            w.SessionStartsIn = () => SessionStartsIn?.Invoke() ?? -1;
            w.SessionWaitTotal = () => DreamParkLobby.TotalSeconds?.Invoke() ?? 0;
            w.PlayerName = id => (PlayerName != null ? PlayerName(id) : null) ?? id;
        }

        /// Run a way to play on this device's board (every headset of the session calls this with the same title /
        /// runner; the director itself only runs on the leader). Replaces whatever ran before.
        public void Run(string locationId, string contentId, RunnerRecord runner, IList<RunnerLevel> levels,
            float widthFt, float depthFt, string sessionId, Transform boardRoot)
        {
            Stop("replaced");
            BoardRoot = boardRoot != null ? boardRoot : transform;
            var g = RunnerBoardGeometry.FromFeet(widthFt, depthFt);
            Director = new RunnerDirector(RunnerWorld.Main, locationId, contentId, runner, levels, g, sessionId);
            if (ShowBoard) DrawGrid(g);
            try { OnRun?.Invoke(this); } catch (Exception e) { Log?.Invoke("[runner] OnRun threw: " + e.Message); }
        }

        /// A title's way to play from its release: the director script is the TextAsset at "{gameId}/Runners/{runnerId}"
        /// (sdk-tools' upload convention; a beta target also tries its main title's id). runnerId "" / "default" or a
        /// script that is not there → the built-in default runner.
        public static async Task<RunnerRecord> LoadRunnerAsync(string contentId, string runnerId, string titleName = null)
        {
            if (string.IsNullOrEmpty(runnerId) || runnerId == RunnerDefaults.BuiltInId) return RunnerDefaults.BuiltIn(titleName);
            var ids = new List<string> { contentId };
            if (!string.IsNullOrEmpty(contentId) && contentId.EndsWith("Beta", StringComparison.Ordinal)) ids.Add(contentId.Substring(0, contentId.Length - 4));
            foreach (var id in ids)
            {
                string address = id + "/Runners/" + runnerId;
                try
                {
                    var ta = RunnerSourceProvider != null ? await RunnerSourceProvider(address) : await address.GetAsset<TextAsset>();
                    if (ta != null && !string.IsNullOrWhiteSpace(ta.text)) return RunnerRecord.FromLua(runnerId, ta.text);
                }
                catch (Exception e) { Log?.Invoke($"[runner] {address}: {e.Message}"); }
            }
            Log?.Invoke($"[runner] {contentId}: no director script for '{runnerId}' in this release — the built-in default runner plays");
            return RunnerDefaults.BuiltIn(titleName);
        }

        /// address → the director TextAsset (null = Addressables). Editor / simulator hook.
        public static Func<string, Task<TextAsset>> RunnerSourceProvider;

        public void Stop(string why)
        {
            if (Director == null) return;
            Log?.Invoke($"[runner] host stops ({why})");
            try { OnStop?.Invoke(this, why); } catch (Exception e) { Log?.Invoke("[runner] OnStop threw: " + e.Message); }
            Director.Dispose();
            Director = null;
            foreach (var r in _roots.Values) if (r != null) Destroy(r.gameObject);
            _roots.Clear();
            _edgeLooks.Clear();
            foreach (var g in _links.Values) if (g != null) Destroy(g);
            _links.Clear();
            if (_grid != null) Destroy(_grid);
            _grid = null;
            DrawVoteZones(null, null);
            _voteHidden.Clear();
            _nu = null; _vote = null; Fallback();
        }

        void Update()
        {
            try { Director?.Tick(); }
            catch (Exception e) { Log?.Invoke("[runner] tick threw: " + e); }
            if (_panel != null && (_vote != null || _nu != null)) Fallback();   // the countdown
            FollowPanel();
            UpdateVoteZones();
        }

        // ── Presence ───────────────────────────────────────────────────

        (bool, float, float) LocalPos()
        {
            if (BoardRoot == null) return (false, 0, 0);
            Vector3? w = PlayerWorldPos?.Invoke();
            if (w == null) { var cam = Camera.main; if (cam == null) return (false, 0, 0); w = cam.transform.position; }
            var p = BoardRoot.InverseTransformPoint(w.Value);
            return (true, p.x, p.z);
        }

        RemotePlayer[] _remotes;
        float _remotesNext;
        const float RemotesRefreshSeconds = 0.5f;

        List<(string, float, float)> RemotePositions()
        {
            var l = new List<(string, float, float)>();
            if (BoardRoot == null) return l;
            // PERF (Nov 11 2026, pf-find): the leader asks for these several times a frame; a scene-wide
            // FindObjectsByType each time was ~1 ms+ per call at 9,000 objects. Who is in the scene is
            // re-read twice a second; where they are is read live below.
            if (_remotes == null || Time.unscaledTime >= _remotesNext || Time.unscaledTime < _remotesNext - RemotesRefreshSeconds)
            {
                _remotes = FindObjectsByType<RemotePlayer>(FindObjectsSortMode.None);
                _remotesNext = Time.unscaledTime + RemotesRefreshSeconds;
            }
            foreach (var r in _remotes)
            {
                if (r == null || string.IsNullOrEmpty(r.id) || !r.Synced) continue;
                var p = BoardRoot.InverseTransformPoint(r.HeadTargetWorld);
                l.Add((r.id, p.x, p.z));
            }
            if (ExtraPlayers != null)
                try
                {
                    foreach (var e in ExtraPlayers() ?? new List<(string, Vector3)>())
                    {
                        if (string.IsNullOrEmpty(e.id)) continue;
                        var p = BoardRoot.InverseTransformPoint(e.world);
                        l.Add((e.id, p.x, p.z));
                    }
                }
                catch (Exception ex) { Log?.Invoke("[runner] ExtraPlayers threw: " + ex.Message); }
            return l;
        }

        // ── Levels ─────────────────────────────────────────────────────

        async void Place(RunnerDirector d, RunnerLoad l, RunnerLevel lv)
        {
            if (BoardRoot == null) return;
            var f = d.Board.Frame(l.tiles);
            var root = new GameObject($"Runner Load {l.id} · {l.level} · tile {string.Join("+", l.tiles.Select(t => t + 1))}");
            root.transform.SetParent(BoardRoot, false);
            root.transform.localPosition = new Vector3(f.cx, 0, f.cz);
            var mark = root.AddComponent<RunnerLevelRoot>();
            mark.loadId = l.id; mark.level = l.level; mark.tiles = (int[])l.tiles.Clone(); mark.director = d;
            // Net identity of everything in the level stops HERE: the same on every headset rendering this load (the board
            // root's own parents differ per device), and a different load — another zone's copy of the same level — never
            // shares state with it.
            root.AddComponent<NetScope>().scopeKey = $"runner|{d.LocationId}|{l.id}|{l.level}";
            // a level authored the other way round turns a quarter so its long side runs along the frame's long side,
            // its portal side (front) toward the board's outside (RunnerBoardRules.FitYaw)
            mark.yawDeg = lv != null ? RunnerBoardRules.FitYaw(lv.widthFt, lv.depthFt, f.w, f.d, f.cx, d.Board.WidthM) : 0f;
            bool turn = Mathf.Abs(mark.yawDeg) > 1f;
            var holder = new GameObject("level");
            holder.transform.SetParent(root.transform, false);
            holder.transform.localRotation = Quaternion.Euler(0, mark.yawDeg, 0);
            _roots[l.id] = mark;
            // the floor vote has the play area: a level placed now waits hidden (it never wakes) until the vote is gone
            bool deferred = _voteZones != null && VoteHidesLevels;
            if (deferred) { holder.SetActive(false); _voteHidden[mark] = true; }
            Vector2 target = turn ? new Vector2(f.d, f.w) : new Vector2(f.w, f.d);
            GameObject prefab = null;
            if (lv != null && !string.IsNullOrEmpty(lv.address))
            {
                try { prefab = LevelProvider != null ? await LevelProvider(lv.address) : await lv.address.GetAsset<GameObject>(); }
                catch (Exception e) { Log?.Invoke($"[runner] level {lv.address} failed to load: {e.Message}"); }
            }
            if (root == null || !_roots.ContainsKey(l.id) || _roots[l.id] != mark) return;   // replaced while loading
            // A big prefab is cloned off the main thread (inactive) and wakes once this frame has activation budget left,
            // as the old LevelAnchor path does — one synchronous Instantiate of a 1–3 MB level used to drop 1–3 s of frames.
            GameObject staged = null;
            if (prefab != null && StagedClone != null)
            {
                try { staged = await StagedClone(prefab); }
                catch (Exception e) { Log?.Invoke($"[runner] {l.level}: staged clone failed, using Instantiate: {e.Message}"); staged = null; }
                if (root == null || !_roots.ContainsKey(l.id) || _roots[l.id] != mark) { if (staged != null) Destroy(staged); return; }
            }
            if (prefab != null)
            {
                GameObject inst;
                if (staged != null)
                {
                    inst = ActivateClone != null ? ActivateClone(staged, holder.transform) : staged;
                    if (inst.transform.parent != holder.transform) inst.transform.SetParent(holder.transform, false);
                }
                else inst = Instantiate(prefab, holder.transform, false);
                inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity;
                var at = inst.GetComponent<AttractionTemplate>();
                if (at != null) { try { at.ApplyFootprint(target); } catch (Exception e) { Log?.Invoke($"[runner] {l.level}: fit to {target} threw: {e.Message}"); } }
            }
            else if (Placeholders) Placeholder(holder.transform, target, l, lv);
            else Log?.Invoke($"[runner] level '{l.level}' has no prefab here (address '{lv?.address}')");
            mark.ready = true;
            if (!_voteHidden.ContainsKey(mark)) { try { OnLevelPlaced?.Invoke(mark); } catch (Exception e) { Log?.Invoke("[runner] OnLevelPlaced threw: " + e.Message); } }
            ApplyEdges();
        }

        void RemoveLoad(RunnerLoad l)
        {
            if (!_roots.TryGetValue(l.id, out var r)) return;
            _roots.Remove(l.id);
            if (r == null) return;
            // its despawn transition snapshots it first (core: AttractionTransition.PlayOut); the Destroy stays immediate.
            // A level the floor vote hid is not on show: it simply goes.
            bool hidden = _voteHidden.Remove(r);
            if (r.ready && !hidden) { try { OnLevelRemoving?.Invoke(r); } catch (Exception e) { Log?.Invoke("[runner] OnLevelRemoving threw: " + e.Message); } }
            Destroy(r.gameObject);
        }

        /// dp.level.complete from any gameplay Lua on this device.
        bool LevelCompleteFrom(GameObject go, string json)
        {
            if (Director == null) return false;
            RunnerLevelRoot r = go != null ? go.GetComponentInParent<RunnerLevelRoot>() : null;
            if (r == null)
            {
                // no object given: the only unfinished level, else the one this player stands in
                var open = Director.Rendered.Where(x => !x.done).ToList();
                RunnerLoad pick = open.Count == 1 ? open[0] : null;
                if (pick == null)
                {
                    var p = LocalPos();
                    int tile = p.Item1 ? Director.Board.TileAt(p.Item2, p.Item3) : -1;
                    pick = open.FirstOrDefault(x => x.Covers(tile));
                }
                if (pick == null) { Log?.Invoke("[runner] dp.level.complete: which level? (pass go = self.gameObject)"); return false; }
                return Report(pick.id, json);
            }
            return Report(r.loadId, json);
        }

        bool Report(int loadId, string json)
        {
            var j = RunnerBoardRules.Parse(json);
            Director.ReportComplete(loadId, RunnerBoardRules.Str(j, "result", "done"), j?.GetField("scores")?.Print(), j?.GetField("data")?.Print());
            return true;
        }

        // ── Edges ──────────────────────────────────────────────────────

        static readonly string[] Sides = { "front", "right", "back", "left" };   // yaw +90° (Unity, y-up) turns local back → right, front → left

        static string RotateSide(string side, float yawDeg)
        {
            int i = Array.IndexOf(Sides, RunnerBoardRules.NormSide(side));
            if (i < 0) return "";
            int steps = Mathf.RoundToInt(yawDeg / 90f);
            return Sides[((i - steps) % 4 + 4) % 4];
        }

        void ApplyEdges()
        {
            if (Director == null) return;
            var g = Director.Board;
            foreach (var r in _roots.Values)
            {
                if (r == null || !r.ready) continue;
                foreach (var s in r.GetComponentsInChildren<MonoBehaviour>(true).OfType<IRunnerEdgeSocket>())
                {
                    var mb = (MonoBehaviour)s;
                    string side = RotateSide(s.EdgeSide, r.yawDeg);
                    if (side == "") continue;
                    Vector3 p = BoardRoot.InverseTransformPoint(mb.transform.position);
                    // a socket sits ON its edge: probe a little back inside its own level so a socket on a seam finds its
                    // level's tile (not the first tile of the seam) — both sides of a shared seam then open together
                    int tile = g.SocketTile(r.tiles, p.x, p.z, side);
                    if (tile < 0) continue;
                    int nb = g.Neighbour(tile, side);
                    string look; bool locked = false;
                    if (nb < 0) look = "closed";                                       // the board's outside
                    else if (r.tiles.Contains(nb)) look = "open";                      // inside one level's span
                    else if (_edgeLooks.TryGetValue(tile + ":" + side, out var e)) { look = e.look; locked = e.locked; }
                    else look = "closed";
                    try { s.ApplyEdge(look, locked); } catch (Exception ex) { Log?.Invoke("[runner] edge socket threw: " + ex.Message); }
                }
            }
        }

        // ── Edge Links (dp.edges.link) ─────────────────────────────────

        /// address → an Edge Link prefab (null = Addressables). Editor / simulator hook.
        public static Func<string, Task<GameObject>> LinkProvider;

        /// Where an Edge Link prefab lives: a full address as given, else "{gameId}/{prefab name}" (ContentProcessor's
        /// address for a plain prefab under Content/{gameId}); a beta target also tries its main title's id.
        public static List<string> LinkAddresses(string contentId, string link)
        {
            var l = new List<string>();
            if (string.IsNullOrWhiteSpace(link)) return l;
            if (link.Contains("/")) { l.Add(link); return l; }
            if (!string.IsNullOrEmpty(contentId)) l.Add(contentId + "/" + link);
            if (!string.IsNullOrEmpty(contentId) && contentId.EndsWith("Beta", StringComparison.Ordinal)) l.Add(contentId.Substring(0, contentId.Length - 4) + "/" + link);
            return l;
        }

        /// The seam midpoint of (tile, side) in the board frame + the outward direction of that side (the link's +Z).
        public static (Vector3 pos, Vector3 outward) SeamPose(RunnerBoardGeometry g, int tile, string side)
        {
            var t = g?.Tile(tile); if (t == null) return (Vector3.zero, Vector3.forward);
            switch (RunnerBoardRules.NormSide(side))
            {
                case "right": return (new Vector3(t.cx + t.w / 2, 0, t.cz), Vector3.right);
                case "left": return (new Vector3(t.cx - t.w / 2, 0, t.cz), Vector3.left);
                case "back": return (new Vector3(t.cx, 0, t.cz + t.d / 2), Vector3.forward);
                default: return (new Vector3(t.cx, 0, t.cz - t.d / 2), Vector3.back);
            }
        }

        async void SpawnLink(RunnerDirector d, int tile, string side, string link)
        {
            if (BoardRoot == null) return;
            string key = tile + ":" + side;
            RemoveLink(key);
            var (pos, outward) = SeamPose(d.Board, tile, side);
            var root = new GameObject($"Runner Link {link} · tile {tile + 1} {side}");
            root.transform.SetParent(BoardRoot, false);
            root.transform.localPosition = pos;
            root.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
            _links[key] = root;
            GameObject prefab = null;
            foreach (var address in LinkAddresses(d.ContentId, link))
            {
                try { prefab = LinkProvider != null ? await LinkProvider(address) : await address.GetAsset<GameObject>(); }
                catch (Exception e) { Log?.Invoke($"[runner] link {address}: {e.Message}"); }
                if (prefab != null) break;
            }
            if (root == null || !_links.TryGetValue(key, out var still) || still != root) return;   // removed while loading
            if (prefab != null)
            {
                var inst = Instantiate(prefab, root.transform, false);
                inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity;
                var el = inst.GetComponentInChildren<global::DreamPark.WaysToPlay.EdgeLink>(true);
                if (el != null && !string.IsNullOrEmpty(el.levelA) && !string.IsNullOrEmpty(el.levelB))
                {
                    var la = RunnerBoardRules.At(d.Loads, tile); var lb = RunnerBoardRules.At(d.Loads, d.Board.Neighbour(tile, side));
                    string want = $"{el.levelA} {(global::DreamPark.WaysToPlay.EdgeSides.Name(el.sideOfA))} ⇄ {el.levelB}";
                    bool ok = la != null && lb != null && Same(la.level, el.levelA) && Same(lb.level, el.levelB) && RunnerBoardRules.NormSide(side) == global::DreamPark.WaysToPlay.EdgeSides.Name(el.sideOfA);
                    if (!ok) Log?.Invoke($"[runner] link '{link}' is authored for {want} but stands between {la?.level} and {lb?.level} ({side} of tile {tile + 1}) — spawned anyway");
                }
            }
            else if (Placeholders) LinkPlaceholder(root.transform, link);
            else Log?.Invoke($"[runner] link '{link}' has no prefab here (tried {string.Join(", ", LinkAddresses(d.ContentId, link))})");
        }

        static bool Same(string level, string authored) =>
            string.Equals(level, authored, StringComparison.OrdinalIgnoreCase) || RunnerBoardRules.Slug(level) == RunnerBoardRules.Slug(authored);

        void RemoveLink(string key)
        {
            if (_links.TryGetValue(key, out var g)) { _links.Remove(key); if (g != null) Destroy(g); }
        }

        void LinkPlaceholder(Transform parent, string link)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = sh != null ? new Material(sh) { color = new Color(0.35f, 0.55f, 1f) } : null;
            void Block(string n, Vector3 p, Vector3 sc)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                b.name = n; var c = b.GetComponent<Collider>(); if (c != null) Destroy(c);
                b.transform.SetParent(parent, false); b.transform.localPosition = p; b.transform.localScale = sc;
                if (m != null) b.GetComponent<Renderer>().sharedMaterial = m;
            }
            Block("post L", new Vector3(-0.7f, 1.1f, 0), new Vector3(0.15f, 2.2f, 0.5f));
            Block("post R", new Vector3(0.7f, 1.1f, 0), new Vector3(0.15f, 2.2f, 0.5f));
            Block("top", new Vector3(0, 2.25f, 0), new Vector3(1.6f, 0.2f, 0.5f));
            var label = new GameObject("label").AddComponent<TextMeshPro>();
            label.transform.SetParent(parent, false);
            label.transform.localPosition = new Vector3(0, 2.6f, 0);
            label.text = link; label.fontSize = 3; label.alignment = TextAlignmentOptions.Center; label.color = Color.white;
            label.rectTransform.sizeDelta = new Vector2(3, 0.6f);
        }

        /// The links spawned on this device ("t:side" → GameObject), for checks.
        public IReadOnlyDictionary<string, GameObject> Links => _links;

        // ── Simulator visuals ──────────────────────────────────────────

        static readonly Color[] Tints = { new Color(0.45f, 0.35f, 1f), new Color(0.2f, 0.8f, 0.7f), new Color(1f, 0.6f, 0.25f), new Color(0.95f, 0.35f, 0.6f), new Color(0.5f, 0.85f, 0.3f) };

        void Placeholder(Transform parent, Vector2 size, RunnerLoad l, RunnerLevel lv)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "placeholder " + l.level;
            var col = box.GetComponent<Collider>(); if (col != null) Destroy(col);
            box.transform.SetParent(parent, false);
            float h = 0.1f + 0.1f * Math.Min(4, Math.Max(1, l.tiles.Length));
            box.transform.localScale = new Vector3(size.x - 0.7f, h, size.y - 0.7f);
            box.transform.localPosition = new Vector3(0, h / 2, 0);
            var rend = box.GetComponent<Renderer>();
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (rend != null && sh != null)
            {
                var m = new Material(sh);
                var c = Tints[Math.Abs(l.id) % Tints.Length];
                m.color = c;
                rend.sharedMaterial = m;
            }
            // the level's portal side (front, −Z): a bright strip, so a turned level shows which way it faces
            var front = GameObject.CreatePrimitive(PrimitiveType.Cube);
            front.name = "front (portal side)";
            var fc = front.GetComponent<Collider>(); if (fc != null) Destroy(fc);
            front.transform.SetParent(parent, false);
            front.transform.localScale = new Vector3(Mathf.Max(0.4f, size.x - 1.2f), h + 0.06f, 0.18f);
            front.transform.localPosition = new Vector3(0, (h + 0.06f) / 2, -(size.y - 0.7f) / 2 + 0.12f);
            var fr = front.GetComponent<Renderer>();
            if (fr != null && sh != null) fr.sharedMaterial = new Material(sh) { color = new Color(1f, 0.92f, 0.3f) };
            var label = new GameObject("label").AddComponent<TextMeshPro>();
            label.transform.SetParent(parent, false);
            label.transform.localPosition = new Vector3(0, h + 0.02f, 0);
            label.transform.localRotation = Quaternion.Euler(90, 0, 0);
            label.text = (lv?.name ?? l.level) + $"\n<size=60%>{l.tiles.Length} tile{(l.tiles.Length == 1 ? "" : "s")}</size>";
            label.fontSize = 4; label.alignment = TextAlignmentOptions.Center; label.color = Color.white;
            label.rectTransform.sizeDelta = new Vector2(size.x - 0.9f, size.y * 0.5f);
            label.enableWordWrapping = true;
        }

        void DrawGrid(RunnerBoardGeometry g)
        {
            _grid = new GameObject("Runner Board Grid");
            _grid.transform.SetParent(BoardRoot, false);
            var sh = Shader.Find("Sprites/Default");
            Material mat = sh != null ? new Material(sh) { color = new Color(1, 1, 1, 0.9f) } : null;
            foreach (var t in g.tiles)
            {
                var lr = new GameObject("tile " + (t.index + 1)).AddComponent<LineRenderer>();
                lr.transform.SetParent(_grid.transform, false);
                lr.useWorldSpace = false; lr.loop = true; lr.positionCount = 4; lr.widthMultiplier = 0.03f;
                if (mat != null) lr.sharedMaterial = mat;
                float x0 = t.cx - t.w / 2 + 0.02f, x1 = t.cx + t.w / 2 - 0.02f, z0 = t.cz - t.d / 2 + 0.02f, z1 = t.cz + t.d / 2 - 0.02f;
                lr.SetPositions(new[] { new Vector3(x0, 0.01f, z0), new Vector3(x1, 0.01f, z0), new Vector3(x1, 0.01f, z1), new Vector3(x0, 0.01f, z1) });
                var num = new GameObject("n").AddComponent<TextMeshPro>();
                num.transform.SetParent(_grid.transform, false);
                num.transform.localPosition = new Vector3(t.cx - t.w / 2 + 0.35f, 0.02f, t.cz - t.d / 2 + 0.35f);
                num.transform.localRotation = Quaternion.Euler(90, 0, 0);
                num.text = (t.index + 1).ToString(); num.fontSize = 6; num.alignment = TextAlignmentOptions.Center; num.color = Color.white;
                num.rectTransform.sizeDelta = new Vector2(0.6f, 0.6f);
            }
        }

        // ── The vote, drawn on the floor ───────────────────────────────
        // GridRunner beta v1 (Oct 27 2026): a floating card read "Pick one · 12 / pitrun · crusher · gemrush / Stand in the zone
        // you want" and nothing showed where the zones were. Aidan (Oct 28): "a default spatialized grid that shows the options
        // with a cool purple outline, fades/grays out the others when you stand in yours and brightens up with magical
        // effects, and above or within each one we say the mode and the description" — in the look of the play area's
        // content-loading effect (HeadsetDownloadCard: the attraction flourish's own Glint + Orb light). So the vote IS the zones:
        //   • each zone: one continuous band of the flourish's orb light round its edge on the floor, its label and description
        //     floating over it (turned to each viewer); faint glints over the whole grid;
        //   • the zone THIS headset's player stands in lights up white-hot, glints twinkle up off it and motes rise (a burst
        //     on stepping in); the others dim to gray; nobody in a zone = all violet;
        //   • a header over the grid: the prompt and the countdown; the winner turns gold with the attraction flourish's burst
        //     and the rest go.
        // Headset-cheap: one band mesh per zone (built once), three capped particle systems for the whole vote whatever the
        // number of options. Every headset draws its own. dp.flow.choose{outline = false} (the title draws the zones: the panel
        // shows instead) or {panel = false} (the title draws the whole vote) turn it off.

        /// Kill switch for the platform's floor vote (false = the floating panel, as before Oct 28).
        public static bool VoteZoneOutlines = true;
        /// The platform draws this vote on the floor (so no floating panel for it).
        public static bool DrawsVote(RunnerVote v) => VoteZoneOutlines && v != null && v.panel && v.outline && v.HasZones;

        // ── The vote's prompt on a screen (Nov 10 2026 playtest, Aidan: "the default one should have our big screen and
        //    narration and everything using that style to kind of unify the look … hey, alright, you're going to pick your
        //    game"). The floor zones stay the way to vote; a screen the platform owns (core: the big screen at the far end of
        //    the play area + Dreamie's line, StageVote) shows the prompt and the options. No screen = the header over the grid.

        /// What a floor vote shows on a screen. One object per vote: mine / secondsLeft / winner update while it stands.
        public sealed class VoteScreenInfo
        {
            public string id, prompt;
            public readonly List<string> labels = new List<string>(), descriptions = new List<string>();
            /// The board root (the play rect's centre on the floor, +Z into the park) and the board's size (metres).
            public Transform board;
            public Vector2 boardSizeM;
            /// This headset's player's zone (-1 none); seconds left (-1 = not counting yet, an unarmed dwell vote); the winner (-1 open).
            public int mine = -1, secondsLeft = -1, winner = -1;
            /// False once the vote has left the floor.
            public bool open = true;
            public string WinnerText => winner >= 0 && winner < labels.Count ? labels[winner] : null;
        }
        /// Called once per new floor vote; true = the screen shows it (so no floating header over the grid).
        public static Func<VoteScreenInfo, bool> VoteScreen;
        /// The vote VoteScreen took has left the floor.
        public static Action VoteScreenHide;
        VoteScreenInfo _voteInfo;
        /// The floor vote a screen shows now (null = none / the header), for checks.
        public VoteScreenInfo VoteOnScreen => _voteInfo;

        void HideVoteScreen()
        {
            if (_voteInfo == null) return;
            _voteInfo.open = false;
            _voteInfo = null;
            try { VoteScreenHide?.Invoke(); } catch (Exception e) { Log?.Invoke("[runner] VoteScreenHide threw: " + e.Message); }
        }

        sealed class ZoneFx
        {
            public int index;
            public GameObject root;
            public Mesh band;
            public Color32[] colors;
            public Transform sign;
            public TextMeshPro label, desc;
            public Vector3 centre, size;   // board frame; size = the box the glints / motes fill (x, z)
            public float signW;
            public int facing = -1;         // the side of the zone its text reads from: 0 front (portal), 1 right, 2 back, 3 left
            public int state = -2;          // 0 normal, 1 mine, -1 grayed, 2 winner
        }
        GameObject _voteZones;
        string _voteZonesKey;
        readonly List<ZoneFx> _zoneFx = new List<ZoneFx>();
        TextMeshPro _voteHead, _voteSub;
        Transform _voteHeader;
        ParticleSystem _zoneAmbient, _zoneGlints, _zoneMotes;
        GameObject _zoneBurst;
        int _myZone = -2, _headSec = -1;
        /// The zone's name + description lie flat inside it, just over the floor (Aidan, Oct 28 2026).
        const float ZoneSignM = 0.03f, HeaderM = 2.1f, BandHalf = 0.08f, BandInset = 0.16f;

        // ── The vote has the play area (Aidan, Oct 28 2026: "the vote grid would replace the attraction that is loaded so
        //    that it's easy to see and no issues with stuff overlapping"). While the platform draws a vote, the board's
        //    levels bounce out (their despawn transition) and wait hidden — their Lua sleeps; a level the director places
        //    meanwhile (the winner's, from onChoice) waits too. When the vote is gone (after the winner's beat) they all
        //    come in with the spawn transition. A title that draws its own vote (panel = false / outline = false) keeps its level.

        /// Kill switch: false = the levels stay on the board under the floor vote (as before Oct 28).
        public static bool VoteHidesLevels = true;
        readonly Dictionary<RunnerLevelRoot, bool> _voteHidden = new Dictionary<RunnerLevelRoot, bool>();
        /// Levels hidden for the floor vote right now, for checks.
        public int LevelsHiddenForVote => _voteHidden.Count;

        static GameObject HolderOf(RunnerLevelRoot r) { var t = r != null ? r.transform.Find("level") : null; return t != null ? t.gameObject : null; }

        void HideLevelsForVote()
        {
            if (!VoteHidesLevels) return;
            foreach (var r in _roots.Values)
            {
                if (r == null || _voteHidden.ContainsKey(r)) continue;
                var h = HolderOf(r);
                if (h == null || !h.activeSelf) continue;
                if (r.ready) { try { OnLevelRemoving?.Invoke(r); } catch (Exception e) { Log?.Invoke("[runner] OnLevelRemoving threw: " + e.Message); } }
                h.SetActive(false);
                _voteHidden[r] = false;   // false = it was on show (comes back with OnLevelReshown)
            }
        }

        void ShowHiddenLevels()
        {
            if (_voteHidden.Count == 0) return;
            var list = new List<KeyValuePair<RunnerLevelRoot, bool>>(_voteHidden);
            _voteHidden.Clear();
            foreach (var kv in list)
            {
                var r = kv.Key;
                if (r == null) continue;
                var h = HolderOf(r);
                if (h != null) h.SetActive(true);
                if (!r.ready) continue;   // still loading: Place calls OnLevelPlaced when it is in
                var hook = kv.Value ? OnLevelPlaced : OnLevelReshown;   // placed under the vote → its first appearance
                try { hook?.Invoke(r); } catch (Exception e) { Log?.Invoke("[runner] level re-show threw: " + e.Message); }
            }
            ApplyEdges();
        }

        /// The floor vote's root (children of the board root, board frame), for checks; MyVoteZone = the zone this
        /// headset's player stands in (-1 none).
        public GameObject VoteZonesRoot => _voteZones;
        public int MyVoteZone => _myZone;

        void DrawVoteZones(RunnerDirector d, RunnerVote v)
        {
            string key = d == null || BoardRoot == null || !DrawsVote(v) ? null : v.id + "|" + v.winner;
            if (key == _voteZonesKey) return;
            bool sameVote = key != null && _voteZonesKey != null && _voteZonesKey.Split('|')[0] == v.id;
            _voteZonesKey = key;
            if (sameVote && v.winner >= 0) { ShowWinner(v); return; }   // the open vote closed: keep the zones, crown the winner
            if (key == null) ShowHiddenLevels();
            else HideLevelsForVote();
            HideVoteScreen();
            if (_voteZones != null) Destroy(_voteZones);
            _voteZones = null; _voteHeader = null; _voteHead = _voteSub = null; _zoneAmbient = _zoneGlints = _zoneMotes = null;
            _zoneFx.Clear(); _myZone = -2; _headSec = -1;
            if (key == null) return;
            _voteZones = new GameObject("Runner Vote Zones");
            _voteZones.transform.SetParent(BoardRoot, false);
            // the attraction flourish's own light (shared, never copied or changed); without it (an SDK project) a plain band
            Material orb = Resources.Load<Material>("AttractionTransitions/AttractionFlourishOrb");
            Material glint = Resources.Load<Material>("AttractionTransitions/AttractionFlourishGlint");
            if (orb != null && (orb.shader == null || !orb.shader.isSupported)) orb = null;
            if (glint != null && (glint.shader == null || !glint.shader.isSupported)) glint = null;
            Material bandMat = orb != null ? orb : new Material(Shader.Find("Sprites/Default"));
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < v.zones.Count && i < v.options.Count; i++)
            {
                var z = v.zones[i];
                Vector3[] loop; Vector3 centre, size;
                if (z.tile >= 0 || (z.w > 0 && z.d > 0 && !float.IsNaN(z.x)))
                {
                    float cx, cz, w, dd;
                    if (z.tile >= 0) { var t = d.Board.Tile(z.tile); if (t == null) continue; cx = t.cx; cz = t.cz; w = t.w; dd = t.d; }
                    else { cx = z.x; cz = z.z; w = z.w; dd = z.d; }
                    float ix = Mathf.Min(BandInset, w / 4), iz = Mathf.Min(BandInset, dd / 4);
                    float x0 = cx - w / 2 + ix, x1 = cx + w / 2 - ix, z0 = cz - dd / 2 + iz, z1 = cz + dd / 2 - iz;
                    loop = new[] { new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1) };
                    centre = new Vector3(cx, 0, cz); size = new Vector3(x1 - x0, 0, z1 - z0);
                }
                else if (!float.IsNaN(z.x))
                {
                    float r = Mathf.Max(0.3f, z.r);
                    loop = new Vector3[72];
                    for (int k = 0; k < loop.Length; k++) { float a = k * Mathf.PI * 2 / loop.Length; loop[k] = new Vector3(z.x + Mathf.Cos(a) * r, 0, z.z + Mathf.Sin(a) * r); }
                    centre = new Vector3(z.x, 0, z.z); size = new Vector3(r * 1.4f, 0, r * 1.4f);
                }
                else continue;   // this option has no zone (phone / dp.board.pick only)
                foreach (var p in loop) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
                var fx = new ZoneFx { index = i, centre = centre, size = size };
                fx.root = new GameObject("zone " + (i + 1) + " " + v.options[i]);
                fx.root.transform.SetParent(_voteZones.transform, false);
                fx.band = ZoneBand(fx.root.transform, loop, bandMat, out fx.colors);
                // the zone's name + description, flat inside it, sized to the zone (1 or 20 options); UpdateVoteZones turns
                // it to read from the side of the zone this headset's player is on
                float signW = Mathf.Clamp(Mathf.Min(size.x, size.z) - 0.2f, 0.5f, 3.2f);
                float scale = Mathf.Clamp(signW / 2.2f, 0.3f, 1.3f);
                fx.signW = signW;
                fx.sign = new GameObject("sign").transform;
                fx.sign.SetParent(fx.root.transform, false);
                fx.sign.localPosition = centre + new Vector3(0, ZoneSignM, 0);
                fx.sign.localRotation = Quaternion.Euler(90, 0, 0);   // flat, reading from the portal until the viewer is known
                fx.label = MagicUI.MagicLook.Label(fx.sign, "label", 340f * scale, Color.white, signW, display: true);
                fx.label.text = "<nobr>" + v.LabelOf(i) + "</nobr>";
                fx.label.enableAutoSizing = true; fx.label.fontSizeMin = 30f; fx.label.fontSizeMax = 340f * scale;
                fx.label.rectTransform.sizeDelta = new Vector2(signW / MagicUI.MagicLook.TextUnitScale, 36f * scale);
                fx.label.transform.localPosition = new Vector3(0, 0.2f * scale, 0);
                string ds = v.DescriptionOf(i);
                if (!string.IsNullOrEmpty(ds))
                {
                    fx.desc = MagicUI.MagicLook.Label(fx.sign, "description", 112f * scale, MagicUI.MagicLook.Lilac, signW);
                    fx.desc.textWrappingMode = TextWrappingModes.Normal;
                    fx.desc.enableAutoSizing = true; fx.desc.fontSizeMin = 24f; fx.desc.fontSizeMax = 112f * scale;
                    fx.desc.alignment = TextAlignmentOptions.Top;
                    fx.desc.rectTransform.sizeDelta = new Vector2(signW / MagicUI.MagicLook.TextUnitScale, 50f * scale);
                    fx.desc.transform.localPosition = new Vector3(0, -0.2f * scale, 0);
                    fx.desc.text = ds;
                }
                _zoneFx.Add(fx);
            }
            if (_zoneFx.Count == 0) return;
            // the light over the grid: faint glints everywhere; this player's zone gets the dense glints + rising motes
            if (glint != null && orb != null)
            {
                Vector3 gridC = new Vector3((minX + maxX) / 2, 0, (minZ + maxZ) / 2), gridS = new Vector3(maxX - minX, 0, maxZ - minZ);
                _zoneAmbient = ZoneSystem(_voteZones.transform, "ambient glints", glint, 90, new Vector2(1.4f, 2.4f), new Vector2(0.10f, 0.22f), new Vector2(0.02f, 0.08f));
                PlaceSystem(_zoneAmbient, gridC, gridS);
                var am = _zoneAmbient.main; am.startColor = FlourishPalette(true, 0.55f);
                var ae = _zoneAmbient.emission; ae.rateOverTime = Mathf.Clamp(4f + 1.2f * gridS.x * gridS.z, 6f, 40f);
                _zoneAmbient.Play();
                _zoneGlints = ZoneSystem(_voteZones.transform, "my zone glints", glint, 120, new Vector2(1.2f, 2.0f), new Vector2(0.14f, 0.32f), new Vector2(0.05f, 0.16f));
                _zoneMotes = ZoneSystem(_voteZones.transform, "my zone motes", orb, 70, new Vector2(1.8f, 3.0f), new Vector2(0.07f, 0.15f), new Vector2(0.25f, 0.5f));
            }
            // the prompt on the platform's screen (core: the big screen + Dreamie's line), else the header over the grid
            var info = new VoteScreenInfo { id = v.id, prompt = v.prompt, board = BoardRoot, boardSizeM = new Vector2(d.Board.WidthM, d.Board.DepthM), winner = v.winner };
            for (int i = 0; i < v.options.Count; i++) { info.labels.Add(v.LabelOf(i)); info.descriptions.Add(v.DescriptionOf(i)); }
            bool onScreen = false;
            if (VoteScreen != null) { try { onScreen = VoteScreen(info); } catch (Exception e) { Log?.Invoke("[runner] VoteScreen threw: " + e.Message); } }
            if (onScreen) { _voteInfo = info; if (v.winner >= 0) ShowWinner(v); else UpdateVoteZones(); return; }
            // the header: what the group is choosing + the countdown, over the back of the grid. Fixed in the board frame,
            // reading from the portal (Nov 10 playtest: "the logo looks weird when it turns to you … this is billboarding")
            _voteHeader = new GameObject("header").transform;
            _voteHeader.SetParent(_voteZones.transform, false);
            _voteHeader.localPosition = new Vector3((minX + maxX) / 2, HeaderM, maxZ + 0.2f);
            _voteHeader.localRotation = Quaternion.identity;
            _voteHead = MagicUI.MagicLook.Label(_voteHeader, "prompt", 300f, Color.white, 6f, display: true);
            _voteHead.transform.localPosition = new Vector3(0, 0.18f, 0);
            _voteHead.text = string.IsNullOrEmpty(v.prompt) ? "Pick one" : v.prompt;
            _voteSub = MagicUI.MagicLook.Label(_voteHeader, "countdown", 140f, MagicUI.MagicLook.Lilac, 6f);
            _voteSub.transform.localPosition = new Vector3(0, -0.2f, 0);
            if (v.winner >= 0) ShowWinner(v);
            else UpdateVoteZones();
        }

        /// One continuous band of light round the loop on the floor (the download field's boundary): ONE strip with mitred
        /// joins (every corner's inner and outer vertex is shared by both edges), the orb light ACROSS the band only (uv.x at
        /// the middle), one twinkle phase for the whole band (uv.z). Nov 10 playtest ("it's got all these sharp edges, it's
        /// not like a smooth line"): it was a separate quad per segment, so round a circle each pair of quads left a thin
        /// wedge gap on the outside and overlapped (double light) on the inside, and uv.z ran 0→1 round the loop: the orb's
        /// twinkle is sin(t·(7 + 11·uv.z) + 61·uv.z), so after minutes of uptime it changed by hundreds of radians from one
        /// vertex to the next — fine radial stripes all the way round. The band breathes through its vertex colours.
        static Mesh ZoneBand(Transform parent, Vector3[] loop, Material mat, out Color32[] colors)
        {
            var v = new List<Vector3>(); var uv = new List<Vector4>(); var tri = new List<int>();
            int L = loop.Length;
            Vector3 Across(Vector3 a, Vector3 b) { Vector3 t = b - a; t.y = 0; t = t.sqrMagnitude > 1e-8f ? t.normalized : Vector3.right; return new Vector3(-t.z, 0, t.x); }
            Vector3 lift = Vector3.up * 0.015f;
            void Pair(Vector3 p, Vector3 off)
            {
                v.Add(p - off + lift); v.Add(p + off + lift);
                uv.Add(new Vector4(0.5f, 0, 0.5f, 0)); uv.Add(new Vector4(0.5f, 1, 0.5f, 0));
                int n = v.Count;
                if (n >= 4) { tri.Add(n - 4); tri.Add(n - 2); tri.Add(n - 1); tri.Add(n - 4); tri.Add(n - 1); tri.Add(n - 3); }
            }
            for (int e = 0; e <= L; e++)
            {
                int i = e % L;
                Vector3 p = loop[i], prev = loop[(i - 1 + L) % L], next = loop[(i + 1) % L];
                // the mitre at this corner: half the band along the bisector of the two edges' normals (≤ 2.5× at sharp corners)
                Vector3 n0 = Across(prev, p), n1 = Across(p, next), mt = n0 + n1;
                mt = mt.sqrMagnitude > 1e-6f ? mt.normalized : n1;
                Pair(p, mt * (BandHalf / Mathf.Max(0.4f, Vector3.Dot(mt, n1))));
                if (e == L) break;
                // a long straight edge: in-between pairs (~0.25 m)
                float len = Vector3.Distance(p, next);
                int k = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
                for (int s = 1; s < k; s++) Pair(Vector3.Lerp(p, next, (float)s / k), n1 * BandHalf);
            }
            var go = new GameObject("band", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            var m = new Mesh { name = "vote zone band" };
            colors = new Color32[v.Count];
            m.SetVertices(v); m.SetUVs(0, uv); m.colors32 = colors; m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            go.GetComponent<MeshFilter>().sharedMesh = m;
            return m;
        }

        /// A capped floor emitter in the download field's style: billboards rising off a box on the floor, a soft noise drift.
        static ParticleSystem ZoneSystem(Transform parent, string name, Material mat, int max, Vector2 life, Vector2 size, Vector2 rise)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true; main.playOnAwake = false; main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startSpeed = 0f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = FlourishPalette(true, 1f);
            var em = ps.emission; em.rateOverTime = 0f;
            var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(1f, 0.04f, 1f);
            var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.Local;
            vel.x = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f); vel.y = new ParticleSystem.MinMaxCurve(rise.x, rise.y); vel.z = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);
            var n = ps.noise; n.enabled = true; n.quality = ParticleSystemNoiseQuality.Low; n.strength = 0.08f; n.frequency = 0.6f; n.scrollSpeed = 0.5f; n.damping = true;
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.8f, 0.65f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
            var sz = ps.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.2f, 1f), new Keyframe(1f, 0.5f)));
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard; r.alignment = ParticleSystemRenderSpace.View;
            r.sharedMaterial = mat;   // the attraction flourish's own shared material (never copied, never changed)
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.maxParticleSize = 0.2f;
            r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color,
                ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomX, ParticleSystemVertexStream.AgePercent });
            return ps;
        }

        static void PlaceSystem(ParticleSystem ps, Vector3 centre, Vector3 size)
        {
            if (ps == null) return;
            ps.transform.localPosition = centre;
            var sh = ps.shape; sh.scale = new Vector3(Mathf.Max(0.3f, size.x - 0.1f), 0.04f, Mathf.Max(0.3f, size.z - 0.1f)); sh.position = new Vector3(0, 0.14f, 0);
        }

        // The attraction flourish's palette (white-hot, violet-white, lilac, violet, a touch of gold and pink; additive, so
        // alpha = brightness), as the download field uses it; gold for the winner.
        static readonly Color FlVioletWhite = new Color(0.92f, 0.86f, 1.00f), FlLilac = new Color(0.80f, 0.66f, 1.00f),
            FlViolet = new Color(0.66f, 0.48f, 1.00f), FlGold = new Color(1.00f, 0.86f, 0.55f), FlPink = new Color(1.00f, 0.66f, 0.90f);
        static ParticleSystem.MinMaxGradient FlourishPalette(bool flourish, float alpha)
        {
            var g = new Gradient { mode = GradientMode.Fixed };
            g.SetKeys(flourish
                    ? new[] { new GradientColorKey(Color.white, 0.25f), new GradientColorKey(FlVioletWhite, 0.55f), new GradientColorKey(FlLilac, 0.73f),
                              new GradientColorKey(FlViolet, 0.86f), new GradientColorKey(FlGold, 0.94f), new GradientColorKey(FlPink, 1f) }
                    : new[] { new GradientColorKey(Color.white, 0.35f), new GradientColorKey(FlGold, 1f) },
                new[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha, 1f) });
            return new ParticleSystem.MinMaxGradient(g) { mode = ParticleSystemGradientMode.RandomColor };
        }

        void SetZoneLook(ZoneFx fx, int state)
        {
            if (fx.state == state) return;
            fx.state = state;
            if (fx.label != null) fx.label.color = state == -1 ? new Color(0.75f, 0.72f, 0.8f, 0.4f) : state == 2 ? new Color(1f, 0.93f, 0.7f) : Color.white;
            if (fx.desc != null) fx.desc.color = state == -1 ? new Color(0.7f, 0.66f, 0.78f, 0.3f) : MagicUI.MagicLook.Lilac;
            if (fx.sign != null) fx.sign.localScale = Vector3.one * (state >= 1 ? 1.15f : state == -1 ? 0.9f : 1f);
            PaintBand(fx, 1f);
        }

        /// The band's light for its state (alpha = brightness on the additive orb), times the slow breath k.
        void PaintBand(ZoneFx fx, float k)
        {
            if (fx.band == null) return;
            Color c; float a;
            switch (fx.state)
            {
                case 1: c = Color.Lerp(FlLilac, Color.white, 0.55f); a = 1f; break;      // mine: white-hot
                case 2: c = Color.Lerp(FlGold, Color.white, 0.25f); a = 1f; break;       // the winner
                case -1: c = new Color(0.55f, 0.52f, 0.62f); a = 0.16f; break;            // grayed out
                default: c = FlViolet; a = 0.75f * k; break;                              // waiting: violet, breathing
            }
            var c32 = new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), (byte)(Mathf.Clamp01(a) * 255));
            for (int i = 0; i < fx.colors.Length; i++) fx.colors[i] = c32;
            fx.band.colors32 = fx.colors;
        }

        /// This player's zone (or the winner's): the dense glints + rising motes over it, a burst on stepping in.
        void LightZone(ZoneFx fx, bool winner)
        {
            if (_zoneGlints == null) return;
            if (fx == null)
            {
                var e0 = _zoneGlints.emission; e0.rateOverTime = 0f;
                var m0 = _zoneMotes.emission; m0.rateOverTime = 0f;
                return;
            }
            PlaceSystem(_zoneGlints, fx.centre, fx.size);
            PlaceSystem(_zoneMotes, fx.centre, fx.size);
            var gm = _zoneGlints.main; gm.startColor = FlourishPalette(!winner, 1f);
            var mm = _zoneMotes.main; mm.startColor = FlourishPalette(!winner, 0.9f);
            float area = Mathf.Max(0.5f, fx.size.x * fx.size.z);
            var e = _zoneGlints.emission; e.rateOverTime = Mathf.Clamp(10f + 9f * area, 12f, 60f);
            var m = _zoneMotes.emission; m.rateOverTime = Mathf.Clamp(5f + 4f * area, 6f, 30f);
            if (!_zoneGlints.isPlaying) _zoneGlints.Play();
            if (!_zoneMotes.isPlaying) _zoneMotes.Play();
            _zoneGlints.Emit(winner ? 40 : 18);
        }

        void ShowWinner(RunnerVote v)
        {
            ZoneFx won = null;
            foreach (var fx in _zoneFx)
            {
                if (fx.index == v.winner) { SetZoneLook(fx, 2); won = fx; }
                else if (fx.root != null) fx.root.SetActive(false);   // the rest go
            }
            if (_zoneAmbient != null) { var ae = _zoneAmbient.emission; ae.rateOverTime = 0f; }
            LightZone(won, true);
            if (won != null) Burst(won);
            if (_voteHead != null) _voteHead.text = v.WinnerText ?? "";
            if (_voteSub != null) _voteSub.text = "The group's pick";
            if (_voteInfo != null) { _voteInfo.winner = v.winner; _voteInfo.mine = -1; }
        }

        /// The payoff: the attraction flourish (the prefab AttractionTransition and the download field play, its "In"
        /// group), silent, over the winner's zone. Absent in an SDK project: the glints alone.
        void Burst(ZoneFx fx)
        {
            if (_zoneBurst == null)
            {
                var prefab = Resources.Load<GameObject>("AttractionTransitions/AttractionFlourish");
                if (prefab == null) return;
                _zoneBurst = Instantiate(prefab, transform);
                _zoneBurst.name = "Vote Winner Burst";
            }
            Transform t = _zoneBurst.transform;
            t.position = _voteZones.transform.TransformPoint(fx.centre + Vector3.up * 0.02f);
            t.rotation = Quaternion.identity;
            t.localScale = Vector3.one * Mathf.Clamp(0.5f * Mathf.Min(fx.size.x, fx.size.z), 0.45f, 1.4f);
            Transform inG = t.Find("In"), outG = t.Find("Out");
            if (inG != null && outG != null) { inG.gameObject.SetActive(true); outG.gameObject.SetActive(false); }
            foreach (var a in _zoneBurst.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;   // silent: the next level's spawn plays its sound
            _zoneBurst.SetActive(true);
            foreach (ParticleSystem ps in _zoneBurst.GetComponentsInChildren<ParticleSystem>(false)) { ps.Clear(true); ps.Play(false); }
        }

        /// Every frame while the floor vote shows: this player's zone, the countdown, the signs facing the viewer, a slow breath.
        void UpdateVoteZones()
        {
            if (_voteZones == null || _vote == null) return;
            var cam = Camera.main;
            if (cam != null)
            {
                // (the header no longer turns to the viewer: Nov 10 playtest, "this is billboarding")
                // the flat text reads from the side of each zone the viewer is on (snapped to its four sides, so it never
                // spins as you walk; standing inside a zone keeps its last side)
                Vector3 me = _voteZones.transform.InverseTransformPoint(cam.transform.position);
                foreach (var fx in _zoneFx)
                {
                    if (fx.sign == null) continue;
                    float dx = me.x - fx.centre.x, dz = me.z - fx.centre.z;
                    float hx = Mathf.Max(0.1f, fx.size.x / 2), hz = Mathf.Max(0.1f, fx.size.z / 2);
                    // in front of / behind the zone → front / back; only beside it (within its depth) → that side
                    int side = Mathf.Abs(dz) > hz ? (dz > 0 ? 2 : 0) : Mathf.Abs(dx) > hx ? (dx > 0 ? 1 : 3) : (fx.facing < 0 ? 0 : fx.facing);
                    if (side == fx.facing) continue;
                    fx.facing = side;
                    // text bottom toward that side: front −Z (yaw 0), right +X (−90), back +Z (180), left −X (90)
                    fx.sign.localRotation = Quaternion.Euler(0, side == 0 ? 0 : side == 1 ? -90 : side == 2 ? 180 : 90, 0) * Quaternion.Euler(90, 0, 0);
                    float w = side == 0 || side == 2 ? fx.size.x : fx.size.z;   // the zone's width as this side reads it
                    float sw = Mathf.Clamp(w - 0.2f, 0.5f, 3.2f);
                    if (fx.label != null) fx.label.rectTransform.sizeDelta = new Vector2(sw / MagicUI.MagicLook.TextUnitScale, fx.label.rectTransform.sizeDelta.y);
                    if (fx.desc != null) fx.desc.rectTransform.sizeDelta = new Vector2(sw / MagicUI.MagicLook.TextUnitScale, fx.desc.rectTransform.sizeDelta.y);
                }
            }
            if (_vote.winner >= 0) return;
            int mine = -1;
            var (ok, x, z) = LocalPos();
            if (ok && Director != null) mine = RunnerDirector.ZoneOf(_vote, true, x, z, Director.Board.TileAt(x, z));
            if (mine != _myZone)
            {
                _myZone = mine;
                ZoneFx lit = null;
                foreach (var fx in _zoneFx) { SetZoneLook(fx, mine < 0 ? 0 : fx.index == mine ? 1 : -1); if (fx.index == mine) lit = fx; }
                LightZone(lit, false);
                if (_zoneAmbient != null) { var ae = _zoneAmbient.emission; ae.rateOverTime = mine < 0 ? Mathf.Clamp(4f + 1.2f * _zoneAmbient.shape.scale.x * _zoneAmbient.shape.scale.z, 6f, 40f) : 3f; }
            }
            int left = (int)Math.Ceiling(Math.Max(0, _vote.endsAt - RunnerWorld.Main.Clock()));
            if (_voteInfo != null) { _voteInfo.mine = mine; _voteInfo.secondsLeft = left > 120 ? -1 : left; }
            if (_voteSub != null && left != _headSec)
            {
                _headSec = left;
                bool waiting = left > 120;   // an unarmed dwell vote: no countdown until someone steps in
                _voteSub.text = mine >= 0 ? (waiting ? $"You picked {_vote.LabelOf(mine)}" : $"You picked {_vote.LabelOf(mine)} · {left}")
                    : waiting ? "Stand in one to vote" : $"Stand in one to vote · {left}";
            }
            // the waiting bands breathe (the download field's pulse)
            float k = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 2.4f);
            foreach (var fx in _zoneFx) if (fx.state == 0) PaintBand(fx, k);
        }

        // The default panel (no GuestCard here): one floating line of text in front of the viewer.
        void Fallback()
        {
            string text = null;
            // an OPEN vote first, then Next up (it announces the winner), then the winner alone
            // (a vote opened with panel = false is the title's own; one the platform draws on the floor needs no panel)
            var vote = _vote != null && _vote.panel && !DrawsVote(_vote) ? _vote : null;
            if (vote != null && vote.winner < 0)
            {
                double left = Math.Max(0, vote.endsAt - RunnerWorld.Main.Clock());
                text = $"<size=60%>{(string.IsNullOrEmpty(vote.prompt) ? vote.HasZones ? "Stand in a zone" : "Pick one" : vote.prompt)} · {Math.Ceiling(left):0}</size>\n{string.Join("  ·  ", vote.DisplayLabels().Select(l => "<nobr>" + l + "</nobr>"))}";
            }
            else if (_nu != null) text = $"<size=60%>Next up</size>\n{_nu.title}" + (string.IsNullOrEmpty(_nu.subtitle) ? "" : $"\n<size=60%>{_nu.subtitle}</size>");
            else if (vote != null) text = $"<size=60%>The group picked</size>\n{vote.WinnerText}";
            if (text == _panelText) return;
            _panelText = text;
            if (text == null) { if (_panel != null) _panel.gameObject.SetActive(false); return; }
            if (_panel == null)
            {
                _panel = new GameObject("Runner Panel").AddComponent<TextMeshPro>();
                _panel.transform.SetParent(transform, false);
                _panel.fontSize = 3; _panel.alignment = TextAlignmentOptions.Center; _panel.color = Color.white;
                _panel.rectTransform.sizeDelta = new Vector2(2.4f, 1.2f);
            }
            _panel.gameObject.SetActive(true);
            _panel.text = text;
        }

        void FollowPanel()
        {
            if (_panel == null || !_panel.gameObject.activeSelf) return;
            var cam = Camera.main; if (cam == null) return;
            var f = cam.transform.forward;
            _panel.transform.position = cam.transform.position + f * 1.6f + cam.transform.up * 0.25f;
            _panel.transform.rotation = Quaternion.LookRotation(f, cam.transform.up);
        }
    }
}
