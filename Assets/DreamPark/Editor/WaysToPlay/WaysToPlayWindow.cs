// ─────────────────────────────────────────────────────────────────────
//  WaysToPlayWindow.cs — Park Simulator ▸ Ways to Play.
//
//  Pick a title and one of its ways to play, a board (1, 2 or 4 tiles) and a
//  number of simulated players, then run the director: the board, its edges
//  (closed / open / locked), the players, the Next-up / vote / results /
//  Thanks panels and the event log update live, and "Show in scene" places
//  the title's real level prefabs on the tiles and switches their Edge
//  Sockets. Buttons simulate a late join, the leader leaving mid-level and a
//  level finishing. Also shows the pre-upload findings and the exact
//  `runners` block the uploader will send (dry run).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.WaysToPlay
{
    public sealed class WaysToPlayWindow : EditorWindow
    {
        const string MenuPath = "DreamPark/Park Simulator/Ways to Play...";

        [MenuItem(MenuPath, false, 20)]
        public static void Open() => GetWindow<WaysToPlayWindow>("Ways to Play").Show();

        // selection
        List<string> roots = new List<string>();
        int rootIndex;
        List<RunnerEntry> runners = new List<RunnerEntry>();
        int runnerIndex;
        List<TitleLevel> levels = new List<TitleLevel>();
        int tilesChoice = 0;                       // 0:1, 1:2, 2:4
        static readonly int[] TileCounts = { 1, 2, 4 };
        static readonly string[] TileLabels = { "1 tile · 12×18", "2 tiles · 24×18", "4 tiles · 24×36" };
        int playerCount = 3;
        float minutes = 6f, levelSeconds = 20f, speed = 4f, pileIn = 30f;
        int seed = 1;
        bool autoComplete = true, showInScene = true, paused;
        int tab;                                   // 0 run, 1 checks, 2 upload export
        Vector2 logScroll, sideScroll, findScroll, exportScroll;
        List<RunnerFinding> findings;
        string exportText;

        readonly WaysToPlaySim sim = new WaysToPlaySim();
        readonly WaysToPlayStage stage = new WaysToPlayStage();
        double lastTick;

        public WaysToPlaySim Sim => sim;
        /// Freeze real-time ticking (scripted runs / screenshots step with Advance).
        public bool Paused { get => paused; set => paused = value; }
        /// 0 Run · 1 Checks · 2 Upload export; showing 1 or 2 also fills it (scripted runs / screenshots).
        public int Tab
        {
            get => tab;
            set
            {
                tab = value;
                if (tab == 1) findings = RunnerChecks.Run(ContentRoot);
                if (tab == 2) exportText = BuildExportText(ContentRoot);
                Repaint();
            }
        }
        public WaysToPlayStage Stage => stage;
        public string ContentRoot => roots.Count > 0 ? roots[Mathf.Clamp(rootIndex, 0, roots.Count - 1)] : "";

        void OnEnable()
        {
            Refresh();
            EditorApplication.update += OnUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            sim.Stop();
            stage.Clear();
        }

        public void Refresh()
        {
            string keep = ContentRoot;
            roots = RunnerCatalog.CandidateRoots();
            int i = roots.IndexOf(keep);
            if (i >= 0) rootIndex = i;
            else { int s = roots.IndexOf(RunnerCatalog.SampleRoot); rootIndex = s >= 0 ? s : 0; }
            LoadTitle();
        }

        void LoadTitle()
        {
            runners = string.IsNullOrEmpty(ContentRoot) ? new List<RunnerEntry>() : RunnerCatalog.Discover(ContentRoot);
            levels = string.IsNullOrEmpty(ContentRoot) ? new List<TitleLevel>() : TitleLevels.Discover(ContentRoot);
            int def = runners.FindIndex(r => r.header.isDefault);
            runnerIndex = def >= 0 ? def : 0;
            findings = null; exportText = null;
        }

        /// Scriptable entry (builders / tests): select a title + runner and start.
        public bool Begin(string contentRoot, string runnerId, int tiles, int players, float sessionMinutes, float secondsPerLevel, float simSpeed, bool scene)
        {
            Refresh();
            int r = roots.IndexOf(contentRoot);
            if (r < 0) return false;
            rootIndex = r; LoadTitle();
            int k = runners.FindIndex(x => x.runnerId == runnerId);
            if (k < 0 && runners.Count > 0 && !string.IsNullOrEmpty(runnerId)) return false;
            runnerIndex = Math.Max(0, k);
            tilesChoice = Array.IndexOf(TileCounts, tiles); if (tilesChoice < 0) tilesChoice = 0;
            playerCount = players; minutes = sessionMinutes; levelSeconds = secondsPerLevel; speed = simSpeed; showInScene = scene;
            return StartSim();
        }

        public bool StartSim()
        {
            sim.tiles = TileCounts[tilesChoice];
            sim.minutes = minutes;
            sim.pileInSeconds = pileIn;
            sim.autoFinishSeconds = autoComplete ? levelSeconds : 0f;
            sim.seed = seed;
            sim.contentId = GameIdFor(ContentRoot);
            sim.levels = levels.Select(l => new RunnerLevel
            {
                id = l.name, name = l.name,
                address = RunnerIds.IsRunnerOnlyAsset(l.assetPath)
                    ? RunnerIds.LevelAddress(sim.contentId, l.name) : sim.contentId + "/Levels/" + l.name,
                widthFt = l.sizeFeet.x, depthFt = l.sizeFeet.y,
            }).ToList();
            RunnerEntry r = runners.Count > 0 ? runners[Mathf.Clamp(runnerIndex, 0, runners.Count - 1)] : null;
            sim.runner = r == null ? null : new RunnerRecord { runnerId = r.runnerId, name = r.header.name, blurb = r.header.blurb, cover = r.header.cover, isDefault = r.header.isDefault, lua = r.source };
            stage.Clear();
            bool ok = sim.Start(playerCount);
            paused = false;
            lastTick = EditorApplication.timeSinceStartup;
            SyncStage();
            Repaint();
            return ok;
        }

        void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)Math.Min(0.25, now - lastTick);
            lastTick = now;
            if (!sim.running || paused || sim.ended) return;
            sim.Tick(dt * speed);
            SyncStage();
            Repaint();
        }

        /// Advance simulated seconds immediately (tests / the Step button).
        public void Advance(float simSeconds)
        {
            sim.Tick(simSeconds);
            SyncStage();
            Repaint();
        }

        public void SyncStage()
        {
            if (showInScene && sim.Observer != null) stage.Sync(sim, levels);
            else stage.Clear();
            SceneView.RepaintAll();
        }

        // ── GUI ────────────────────────────────────────────────────
        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            sideScroll = EditorGUILayout.BeginScrollView(sideScroll, GUILayout.Width(300));
            DrawSettings();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginVertical();
            tab = GUILayout.Toolbar(tab, new[] { "Run", "Checks", "Upload export (dry run)" });
            if (tab == 0) DrawRun();
            else if (tab == 1) DrawChecks();
            else DrawExport();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        void DrawSettings()
        {
            EditorGUILayout.LabelField("Title", EditorStyles.boldLabel);
            if (roots.Count == 0) { EditorGUILayout.HelpBox("No titles under Assets/Content.", MessageType.Info); return; }
            int ni = EditorGUILayout.Popup(rootIndex, roots.Select(r => r.Replace("Assets/Content/", "").Replace("Assets/DreamPark/Samples/", "Sample: ")).ToArray());
            if (ni != rootIndex) { rootIndex = ni; LoadTitle(); }
            if (GUILayout.Button("Reload title")) LoadTitle();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Way to play", EditorStyles.boldLabel);
            if (runners.Count == 0) EditorGUILayout.HelpBox("No Runners/*.lua.txt: the built-in default plays the levels in order, one per tile.", MessageType.None);
            else
            {
                runnerIndex = EditorGUILayout.Popup(runnerIndex, runners.Select(r => (r.header.Ok ? r.header.name : r.runnerId + " (error)") + (r.header.isDefault ? "  ★" : "")).ToArray());
                RunnerEntry r = runners[Mathf.Clamp(runnerIndex, 0, runners.Count - 1)];
                if (!r.header.Ok) EditorGUILayout.HelpBox(r.header.error, MessageType.Error);
                else
                {
                    EditorGUILayout.LabelField(r.header.blurb, EditorStyles.wordWrappedMiniLabel);
                    var cover = string.IsNullOrEmpty(r.coverAssetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(r.coverAssetPath);
                    if (cover != null) GUILayout.Label(cover, GUILayout.Width(160), GUILayout.Height(90));
                }
                if (GUILayout.Button("Open script")) AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<TextAsset>(r.assetPath));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Board & players", EditorStyles.boldLabel);
            tilesChoice = GUILayout.SelectionGrid(tilesChoice, TileLabels, 1);
            playerCount = EditorGUILayout.IntSlider("Players", playerCount, 1, 8);
            minutes = EditorGUILayout.Slider("Session (min)", minutes, 1f, 30f);
            levelSeconds = EditorGUILayout.Slider("A level lasts (s)", levelSeconds, 5f, 300f);
            pileIn = EditorGUILayout.Slider(new GUIContent("Pile-in (s)", "The play session's wait before the shared clock starts (dp.session.onClockStart). 0 = none."), pileIn, 0f, 60f);
            autoComplete = EditorGUILayout.Toggle("Levels finish by themselves", autoComplete);
            speed = EditorGUILayout.Slider("Speed ×", speed, 0.5f, 20f);
            bool s = EditorGUILayout.Toggle("Show in scene", showInScene);
            if (s != showInScene) { showInScene = s; SyncStage(); }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Levels ({levels.Count})", EditorStyles.boldLabel);
            foreach (var l in levels)
                EditorGUILayout.LabelField($"{l.name}", $"{l.footprintTiles} tile{(l.footprintTiles > 1 ? "s" : "")} · {l.sockets.Count} sockets", EditorStyles.miniLabel);
        }

        void DrawRun()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(sim.running ? "Restart" : "Start", GUILayout.Height(24))) StartSim();
            GUI.enabled = sim.running && !sim.ended;
            if (GUILayout.Button(paused ? "Resume" : "Pause", GUILayout.Height(24))) paused = !paused;
            if (GUILayout.Button("Step 5s", GUILayout.Height(24))) Advance(5f);
            GUI.enabled = sim.running;
            if (GUILayout.Button("Stop", GUILayout.Height(24))) { sim.Stop(); stage.Clear(); }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = sim.running && !sim.ended;
            if (GUILayout.Button("Late join")) sim.AddPlayer();
            if (GUILayout.Button("Leader leaves")) sim.LeaderLeaves();
            if (GUILayout.Button("Session: 1 min left")) sim.timeLeft = Math.Min(sim.timeLeft, 61);
            if (GUILayout.Button("Clock: start now")) sim.pileInSeconds = (float)Math.Max(0.01, sim.Elapsed);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            Rect area = GUILayoutUtility.GetRect(10, 10000, 260, 440);
            DrawBoard(area);

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = sim.running && !sim.ended;
            foreach (var l in sim.Loads.Where(l => !l.done))
            {
                string where = $"tile {string.Join("+", l.tiles.Select(x => x + 1))}{(l.InMain ? "" : ", zone " + l.zone)}";
                if (GUILayout.Button($"Finish {l.level} ({where})")) sim.FinishLoad(l, "won");
                if (GUILayout.Button("Fail", GUILayout.Width(40))) sim.FinishLoad(l, "lost");
            }
            RunnerVote v = sim.Vote;
            if (v != null && v.winner < 0)
                for (int i = 0; i < v.options.Count; i++)
                    if (GUILayout.Button($"All vote {v.LabelOf(i)}")) sim.EveryoneVote(i);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("dp.flow.state  " + sim.ScriptState, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(sim.LastError)) EditorGUILayout.HelpBox("Director: " + sim.LastError, MessageType.Error);

            EditorGUILayout.LabelField("Event log (★ = the leader, where the director runs)", EditorStyles.boldLabel);
            logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.MinHeight(140));
            for (int i = sim.log.Count - 1; i >= 0; i--)
            {
                string line = sim.log[i];
                var style = line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || line.Contains("threw") ? Styles.Error : line.Contains("WARN") ? Styles.Warn : EditorStyles.miniLabel;
                EditorGUILayout.LabelField(line, style);
            }
            EditorGUILayout.EndScrollView();
        }

        static readonly string[] SideNames = { "front", "back", "right", "left" };

        void DrawBoard(Rect area)
        {
            EditorGUI.DrawRect(area, new Color(0.13f, 0.13f, 0.15f));
            var g = sim.Geometry;
            if (g == null || sim.Observer == null)
            {
                GUI.Label(area, sim.running ? "Waiting for the headsets to sync…" : "Press Start to run the way to play.", Styles.Center);
                return;
            }
            string status = sim.ended ? "Session over" : $"{WaysToPlaySim.FormatTime(sim.timeLeft)} left · leader {sim.LeaderId} · {sim.Players} headsets" +
                            (sim.leaderChanges > 0 ? $" · {sim.leaderChanges} leader change(s)" : "") + (sim.lateJoins > 0 ? $" · {sim.lateJoins} late join(s)" : "") +
                            $" · {(sim.runner != null ? sim.runner.name : "built-in default")}";
            GUI.Label(new Rect(area.x + 8, area.y + 4, area.width - 16, 18), status, Styles.White);

            float pad = 30f;
            float ft = Mathf.Min((area.width - pad * 2) / sim.WidthFt, (area.height - pad * 2 - 46) / sim.DepthFt);
            float bw = sim.WidthFt * ft, bd = sim.DepthFt * ft;
            Rect b = new Rect(area.x + (area.width - bw) * 0.5f, area.y + 30 + (area.height - 76 - bd) * 0.5f, bw, bd);
            GUI.Label(new Rect(b.x, b.yMax + 4, b.width, 14), "▼ portal (front)", Styles.MiniCenter);

            // the main board; zones are listed under it (their players see them on the same tiles)
            List<RunnerLoad> loads = sim.Loads.Where(l => l.InMain).ToList();
            var zoneLoads = sim.Loads.Where(l => !l.InMain).ToList();
            if (zoneLoads.Count > 0)
                GUI.Label(new Rect(area.x + 8, area.yMax - 18, area.width - 16, 16),
                    "Zones: " + string.Join("  ·  ", zoneLoads.GroupBy(l => l.zone).Select(zg => zg.Key + " = " + string.Join(", ", zg.Select(l => l.level + "@" + string.Join("+", l.tiles.Select(x => x + 1)))) +
                        " [" + string.Join(",", sim.Live.Where(h => sim.ZoneOf(h) == zg.Key).Select(h => h.id)) + "]")), Styles.MiniWhite);
            List<RunnerPending> pending = sim.Pending;
            for (int i = 0; i < g.Count; i++)
            {
                Rect r = TileRect(b, g, i, ft);
                RunnerLoad l = loads.FirstOrDefault(x => x.Covers(i));
                Color fill = l == null ? new Color(0.22f, 0.22f, 0.25f) : l.done ? new Color(0.25f, 0.36f, 0.25f) : new Color(0.3f, 0.24f, 0.45f);
                EditorGUI.DrawRect(r, fill);
                GUI.Label(new Rect(r.x + 4, r.y + 4, r.width - 8, 18), $"{i + 1}", Styles.MiniWhite);
            }
            // One label per load, centred on its span.
            foreach (var l in loads)
            {
                Rect u = TileRect(b, g, l.tiles[0], ft);
                foreach (int ti in l.tiles) { Rect r = TileRect(b, g, ti, ft); u = Rect.MinMaxRect(Mathf.Min(u.xMin, r.xMin), Mathf.Min(u.yMin, r.yMin), Mathf.Max(u.xMax, r.xMax), Mathf.Max(u.yMax, r.yMax)); }
                string sub = l.done ? (pending.Any(p => p.kind == RunnerPending.Transform || l.tiles.Contains(p.tile)) ? "done · swap waiting" : "done · free play")
                    : sim.autoFinishSeconds > 0 ? $"{sim.PlayedSeconds(l.id):0}/{sim.autoFinishSeconds:0}s" : "playing";
                GUI.Label(new Rect(u.x, u.center.y - 20, u.width, 40), l.level + (l.tiles.Length > 1 ? $" ({l.tiles.Length} tiles)" : "") + "\n" + sub, Styles.CenterWhite);
            }

            // Edges (the observer headset's looks).
            for (int i = 0; i < g.Count; i++)
                foreach (string side in SideNames)
                {
                    int nb = g.Neighbour(i, side);
                    var (look, locked) = sim.EdgeLook(i, side);
                    RunnerLoad mine = loads.FirstOrDefault(x => x.Covers(i));
                    if (nb >= 0 && mine != null && mine.Covers(nb)) continue;   // inside one level: no edge
                    DrawEdge(TileRect(b, g, i, ft), side, look, locked, ft);
                }

            // Players (headsets): the leader in gold.
            var byTile = sim.Live.GroupBy(h => h.Tile).ToList();
            foreach (var grp in byTile)
            {
                if (grp.Key < 0) continue;
                Rect r = TileRect(b, g, grp.Key, ft);
                int k = 0;
                foreach (var h in grp)
                {
                    float x = r.x + 14 + (k % 5) * 22, y = r.yMax - 22 - (k / 5) * 20;
                    bool lead = h == sim.Leader;
                    EditorGUI.DrawRect(new Rect(x, y, 12, 12), lead ? new Color(1f, 0.85f, 0.2f) : new Color(0.7f, 0.85f, 1f));
                    GUI.Label(new Rect(x - 4, y - 14, 34, 14), h.id, Styles.MiniWhite);
                    k++;
                }
            }

            // Panels: vote / Next up (the platform's, from the shared table).
            RunnerVote v = sim.Vote;
            RunnerNextUp nu = sim.NextUp;
            string panel = null; Color pc = default;
            if (v != null)
            {
                double left = Math.Max(0, v.endsAt - sim.Observer.engine.Now);
                panel = (v.winner >= 0 ? $"Vote: {v.WinnerText} wins" : $"Vote: {string.Join(" / ", v.DisplayLabels())} · {left:0}s · stand in a zone")
                    + (v.panel ? "" : "  (panel = false: the title draws its own vote)");
                pc = new Color(0.6f, 0.35f, 0.9f);
            }
            else if (nu != null) { panel = $"Next up: {nu.title}" + (string.IsNullOrEmpty(nu.subtitle) ? "" : " — " + nu.subtitle); pc = new Color(0.25f, 0.55f, 0.95f); }
            else if (sim.ended) { panel = "Final results → Thanks for playing!"; pc = new Color(0.9f, 0.5f, 0.2f); }
            if (panel != null)
            {
                Rect pr = new Rect(area.x + area.width * 0.15f, area.y + 22, area.width * 0.7f, 26);
                EditorGUI.DrawRect(pr, pc);
                GUI.Label(pr, panel, Styles.CenterWhiteBold);
            }
        }

        /// Front row (row 0, toward the portal) at the BOTTOM: the board as seen standing at the portal looking in.
        static Rect TileRect(Rect b, RunnerBoardGeometry g, int i, float ft)
        {
            RunnerTile t = g.Tile(i);
            float w = RunnerBoardGeometry.TileWidthFt * ft, d = RunnerBoardGeometry.TileDepthFt * ft;
            return new Rect(b.x + t.col * w, b.yMax - (t.row + 1) * d, w, d);
        }

        static void DrawEdge(Rect r, string side, string look, bool locked, float ft)
        {
            bool open = !locked && look != "closed";
            Color c = locked ? new Color(1f, 0.7f, 0.15f) : open ? new Color(0.25f, 0.9f, 0.45f) : new Color(0.95f, 0.3f, 0.3f);
            float w = 4f, door = EdgeSocket.StandardWidthFeet * ft;
            bool horizontal = side == "front" || side == "back";
            Rect line = side == "front" ? new Rect(r.x, r.yMax - w * 0.5f, r.width, w)
                      : side == "back" ? new Rect(r.x, r.y - w * 0.5f, r.width, w)
                      : side == "right" ? new Rect(r.xMax - w * 0.5f, r.y, w, r.height)
                      : new Rect(r.x - w * 0.5f, r.y, w, r.height);
            if (open)
            {
                if (horizontal)
                {
                    float mid = line.center.x;
                    EditorGUI.DrawRect(new Rect(line.x, line.y, mid - door * 0.5f - line.x, w), c);
                    EditorGUI.DrawRect(new Rect(mid + door * 0.5f, line.y, line.xMax - mid - door * 0.5f, w), c);
                }
                else
                {
                    float mid = line.center.y;
                    EditorGUI.DrawRect(new Rect(line.x, line.y, w, mid - door * 0.5f - line.y), c);
                    EditorGUI.DrawRect(new Rect(line.x, mid + door * 0.5f, w, line.yMax - mid - door * 0.5f), c);
                }
                if (look != "open") GUI.Label(new Rect(line.center.x - 30, line.center.y - 8, 60, 16), look, Styles.MiniCenter);
            }
            else EditorGUI.DrawRect(line, c);
            if (locked) GUI.Label(new Rect(line.center.x - 30, line.center.y - 8, 60, 16), "locked", Styles.MiniCenter);
        }

        void DrawChecks()
        {
            if (GUILayout.Button("Run pre-upload checks for ways to play", GUILayout.Height(24)))
                findings = RunnerChecks.Run(ContentRoot);
            if (findings == null) { EditorGUILayout.HelpBox("The same checks run in the Content Uploader's pre-upload pass.", MessageType.None); return; }
            int blocking = findings.Count(f => f.severity == RunnerFindingSeverity.Blocking);
            EditorGUILayout.HelpBox(blocking > 0 ? $"{blocking} blocking finding(s)" : "No blocking findings", blocking > 0 ? MessageType.Error : MessageType.Info);
            findScroll = EditorGUILayout.BeginScrollView(findScroll);
            foreach (var f in findings.OrderByDescending(f => f.severity))
            {
                MessageType mt = f.severity == RunnerFindingSeverity.Blocking ? MessageType.Error : f.severity == RunnerFindingSeverity.Warning ? MessageType.Warning : MessageType.Info;
                EditorGUILayout.HelpBox(f.message + (string.IsNullOrEmpty(f.assetPath) ? "" : "\n" + f.assetPath), mt);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawExport()
        {
            if (GUILayout.Button("Build the runners block the uploader sends", GUILayout.Height(24)))
                exportText = BuildExportText(ContentRoot);
            if (exportText == null) { EditorGUILayout.HelpBox("commitUpload carries this as the root field `runners` (and manifest.runners). Covers upload right after the commit.", MessageType.None); return; }
            exportScroll = EditorGUILayout.BeginScrollView(exportScroll);
            EditorGUILayout.TextArea(exportText, Styles.Mono, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        public static string GameIdFor(string contentRoot) =>
            contentRoot == RunnerCatalog.SampleRoot ? "WaysToPlaySample" : Path.GetFileName(contentRoot.TrimEnd('/'));

        public static string BuildExportText(string contentRoot)
        {
            var json = RunnerCatalog.BuildExportJson(contentRoot, out List<string> problems);
            string head = json == null ? "// no Runners folder → no `runners` field (the server serves its implicit default)\n" : "// commitUpload body, top level:  \"runners\": …   (covers are uploaded first; null until then)\n";
            foreach (string p in problems) head += "// NOT EXPORTED: " + p + "\n";
            return head + (json != null ? json.Print(true) : "");
        }

        static class Styles
        {
            public static readonly GUIStyle White = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.white } };
            public static readonly GUIStyle MiniWhite = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white } };
            public static readonly GUIStyle Center = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            public static readonly GUIStyle MiniCenter = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
            public static readonly GUIStyle CenterWhite = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.white } };
            public static readonly GUIStyle CenterWhiteBold = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            public static readonly GUIStyle Error = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.45f, 0.45f) } };
            public static readonly GUIStyle Warn = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.8f, 0.3f) } };
            public static readonly GUIStyle Mono = new GUIStyle(EditorStyles.textArea) { font = Font.CreateDynamicFontFromOSFont("Menlo", 11), wordWrap = false };
        }
    }

    /// Places the title's real level prefabs on the simulated board (scene view, not saved) the way the
    /// observer headset renders it, and dresses their Edge Sockets with the runtime's edge looks.
    public sealed class WaysToPlayStage
    {
        const string RootName = "~WaysToPlaySim";
        GameObject root;
        readonly Dictionary<int, GameObject> placed = new Dictionary<int, GameObject>();    // load id → instance
        readonly Dictionary<int, string> placedKey = new Dictionary<int, string>();
        readonly Dictionary<string, GameObject> playerObjects = new Dictionary<string, GameObject>();
        int placedTiles = -1;

        public GameObject Root => root;
        public IEnumerable<GameObject> Instances => placed.Values.Where(g => g != null);

        public void Clear()
        {
            if (root == null) root = GameObject.Find(RootName);
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            root = null; placed.Clear(); placedKey.Clear(); playerObjects.Clear(); placedTiles = -1; services = null; servicesFor = null;
        }

        public void Sync(WaysToPlaySim sim, List<TitleLevel> levels)
        {
            RunnerBoardGeometry g = sim.Geometry;
            if (g == null) return;
            if (root == null || placedTiles != g.Count)
            {
                Clear();
                root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
                placedTiles = g.Count;
                for (int i = 0; i < g.Count; i++)
                {
                    RunnerTile t = g.Tile(i);
                    var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    floor.name = "tile " + (i + 1);
                    floor.hideFlags = HideFlags.DontSave;
                    floor.transform.SetParent(root.transform, false);
                    floor.transform.localPosition = new Vector3(t.cx, -0.01f, t.cz);
                    floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    floor.transform.localScale = new Vector3(t.w - 0.06f, t.d - 0.06f, 1f);
                    UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
                    floor.GetComponent<MeshRenderer>().sharedMaterial = TileMaterial();
                }
            }

            // what the observer headset sees (its zone's levels + the main board where its zone has none)
            List<RunnerLoad> loads = sim.SeenLoads;
            var alive = new HashSet<int>(loads.Select(l => l.id));
            SyncTitleServices(sim);
            foreach (int id in placed.Keys.ToList())
                if (!alive.Contains(id)) { if (placed[id] != null) UnityEngine.Object.DestroyImmediate(placed[id]); placed.Remove(id); placedKey.Remove(id); }
            foreach (var l in loads)
            {
                string key = l.level + "@" + string.Join(",", l.tiles);
                if (placedKey.TryGetValue(l.id, out string have) && have == key && placed[l.id] != null) continue;
                if (placed.TryGetValue(l.id, out var old) && old != null) UnityEngine.Object.DestroyImmediate(old);
                TitleLevel lvl = levels.FirstOrDefault(x => string.Equals(x.name, l.level, StringComparison.OrdinalIgnoreCase));
                if (lvl == null || lvl.prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(lvl.prefab, root.transform);
                inst.hideFlags = HideFlags.DontSave;
                var f = g.Frame(l.tiles);
                inst.transform.localPosition = new Vector3(f.cx, 0f, f.cz);
                // as RunnerHost places it: turned a quarter when its long side runs the other way, then fitted to its
                // frame through its layout frames (a 1-tile level across 2 / 4 tiles picks a bigger frame when it has one)
                float yaw = RunnerBoardRules.FitYaw(lvl.sizeFeet.x, lvl.sizeFeet.y, f.w, f.d, f.cx, g.WidthM);
                inst.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                var at = inst.GetComponent<AttractionTemplate>();
                if (at != null)
                {
                    bool turn = Mathf.Abs(yaw) > 1f;
                    try { at.ApplyFootprint(turn ? new Vector2(f.d, f.w) : new Vector2(f.w, f.d)); }
                    catch (Exception e) { Debug.LogWarning($"[WaysToPlay] {lvl.name}: layout frame fit threw: {e.Message}"); }
                }
                placed[l.id] = inst; placedKey[l.id] = key;
            }

            // Edge sockets: the same rule RunnerHost.ApplyEdges uses (socket → its tile → the neighbour across its side).
            foreach (var kv in placed)
            {
                if (kv.Value == null) continue;
                RunnerLoad l = loads.FirstOrDefault(x => x.id == kv.Key);
                if (l == null) continue;
                foreach (var s in EdgeSockets.All(kv.Value))
                {
                    // Probe a little INSIDE the level (against the socket's outward +Z): a socket sitting exactly on a
                    // seam between two tiles would otherwise resolve to whichever tile TileAt checks first.
                    Vector3 p = root.transform.InverseTransformPoint(s.transform.position - s.transform.forward * 0.15f);
                    float px = Mathf.Clamp(p.x, -g.WidthM / 2 + 0.05f, g.WidthM / 2 - 0.05f), pz = Mathf.Clamp(p.z, -g.DepthM / 2 + 0.05f, g.DepthM / 2 - 0.05f);
                    int tile = g.TileAt(px, pz);
                    if (tile < 0 || !l.Covers(tile)) continue;
                    var (look, locked) = sim.EdgeLook(tile, s.SideName);
                    ((IRunnerEdgeSocket)s).ApplyEdge(look, locked);
                }
            }

            // Headsets as capsules (the leader gold).
            var seen = new HashSet<string>();
            foreach (var h in sim.Live)
            {
                seen.Add(h.id);
                if (!playerObjects.TryGetValue(h.id, out var go) || go == null)
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    go.hideFlags = HideFlags.DontSave;
                    UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                    go.transform.SetParent(root.transform, false);
                    go.transform.localScale = new Vector3(0.35f, 0.85f, 0.35f);
                    playerObjects[h.id] = go;
                }
                go.transform.localPosition = new Vector3(h.x, 0.85f, h.z);
                string zn = sim.ZoneOf(h);
                go.name = h.id + (zn != "main" ? " (zone " + zn + ")" : "");
                go.GetComponent<MeshRenderer>().sharedMaterial = h == sim.Leader ? LeaderMaterial() : PlayerMaterial();
            }
            foreach (var id in playerObjects.Keys.ToList())
                if (!seen.Contains(id)) { if (playerObjects[id] != null) UnityEngine.Object.DestroyImmediate(playerObjects[id]); playerObjects.Remove(id); }
        }

        /// The title's Game Manager + Player rig, as the headset stands them on a runner board (once, outside every tile).
        GameObject services; string servicesFor;
        void SyncTitleServices(WaysToPlaySim sim)
        {
            if (root == null || (services != null && servicesFor == sim.contentId)) return;
            if (services != null) UnityEngine.Object.DestroyImmediate(services);
            servicesFor = sim.contentId;
            services = new GameObject("[Ways to Play] Title services (Game Manager + Player)") { hideFlags = HideFlags.DontSave };
            services.transform.SetParent(root.transform, false);
            string src = sim.contentId != null && sim.contentId.EndsWith("Beta", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder("Assets/Content/" + sim.contentId)
                ? sim.contentId.Substring(0, sim.contentId.Length - 4) : sim.contentId;
            var host = services.AddComponent<DreamParkPackageHost>();
            host.kind = DreamParkPackageKind.Arena; host.contentId = sim.contentId;
#if !DREAMPARKCORE
            var gm = AssetDatabase.LoadAssetAtPath<GameObject>(global::DreamPark.Editor.DreamSequenceGenerator.ContainerPrefabPath(src));
#else
            var gm = AssetDatabase.FindAssets("\"Game Container\" t:Prefab", new[] { "Assets/Content/" + src })
                .Select(AssetDatabase.GUIDToAssetPath).Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p)).FirstOrDefault(g => g != null);
#endif
            if (gm != null) { var g = (GameObject)PrefabUtility.InstantiatePrefab(gm, services.transform); g.name = "Game Manager"; g.hideFlags = HideFlags.DontSave; host.container = g; }
            string root0 = "Assets/Content/" + src;
            if (AssetDatabase.IsValidFolder(root0))
            {
                var player = AssetDatabase.FindAssets("t:Prefab", new[] { root0 }).Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderBy(p => string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), "Player", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p)).FirstOrDefault(g => g != null && g.GetComponent<PlayerRig>() != null && !g.name.StartsWith("Phone", StringComparison.OrdinalIgnoreCase));
                if (player != null) { var p = (GameObject)PrefabUtility.InstantiatePrefab(player, services.transform); p.hideFlags = HideFlags.DontSave; }
            }
        }

        static Material tileMat, playerMat, leaderMat;
        static Material Make(Color c)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            var m = new Material(sh) { hideFlags = HideFlags.DontSave };
            m.color = c;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            return m;
        }
        static Material TileMaterial() => tileMat != null ? tileMat : (tileMat = Make(new Color(0.18f, 0.18f, 0.22f)));
        static Material PlayerMaterial() => playerMat != null ? playerMat : (playerMat = Make(new Color(0.55f, 0.75f, 1f)));
        static Material LeaderMaterial() => leaderMat != null ? leaderMat : (leaderMat = Make(new Color(1f, 0.8f, 0.15f)));
    }
}
