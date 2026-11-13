#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// PLAYLIST STUDIO (Content Uploader → Playlists → Open Playlist Studio; DreamPark → Playlist Studio).
    /// Library on the left (every attraction: preview, size ladder, challenges, badges; drag into a leg),
    /// tabs on the right: Playlists (the visual editor with the live arc, validation and the fit matrix),
    /// Campaign (chapters & unlocks as a map) and Generate (Claude via DreamPark-Web, remix, organizer migration).
    /// Edits are a working copy until Save; the file is Assets/Content/{id}/.dreampark-playlists.json.
    /// </summary>
    public sealed partial class PlaylistStudioWindow : EditorWindow
    {
        const string DragKey = "DreamPark.PlaylistStudio.Attraction";
        static readonly string[] Tabs = { "Playlists", "Edit", "Campaign", "Generate", "Script" };
        public const int HomeTab = 0, EditTab = 1, CampaignTab = 2, GenerateTab = 3, ScriptTab = 4;
        const string TipPref = "DreamPark.PlaylistStudio.TipDismissed";

        [SerializeField] string contentId;
        [SerializeField] int tab;
        [SerializeField] int selected = -1;
        [SerializeField] string search = "";
        [SerializeField] int roleFilter;
        [SerializeField] bool smallRoomOnly;
        [SerializeField] int generateCount = 10;
        [SerializeField] string remixVariant = "";

        PlaylistStudioSession session;
        PlaylistProposal editingProposal;          // non-null: the editor edits a generated proposal, not the file
        Vector2 libScroll, mainScroll, genScroll;
        List<PlaylistIssue> issues = new List<PlaylistIssue>();
        Dictionary<PackagePlaylist, List<PlaylistFitRow>> fitCache = new Dictionary<PackagePlaylist, List<PlaylistFitRow>>();
        string fitHash;
        bool generating, addingKind;
        string status, newKindWord = "";
        static readonly string[] RoleFilters = { "All roles", "start", "stage", "miniboss", "boss", "end", "display", "prop" };

        [MenuItem("DreamPark/Playlist Studio", priority = 41)]
        public static void OpenFromMenu() => Open(PlaylistStudioHeadless.Titles().FirstOrDefault());

        public static PlaylistStudioWindow Open(string contentId, int tab = 0)
        {
            var w = GetWindow<PlaylistStudioWindow>("Playlist Studio");
            w.minSize = new Vector2(980f, 620f);
            if (!string.IsNullOrEmpty(contentId) && contentId != w.contentId) { w.contentId = contentId; w.session = null; w.selected = -1; }
            w.tab = tab;
            w.Show();
            return w;
        }

        /// For screenshot builders: scroll the editor pane.
        public void ScrollEditorTo(float y) { mainScroll = new Vector2(0f, y); Repaint(); }

        /// For tests and screenshot builders: show a prepared session (no AssetDatabase scan).
        public static PlaylistStudioWindow OpenSession(PlaylistStudioSession s, int tab = 0, int select = 0)
        {
            var w = GetWindow<PlaylistStudioWindow>("Playlist Studio");
            w.minSize = new Vector2(980f, 620f);
            w.contentId = s.titleId; w.session = s; w.tab = tab; w.selected = select; w.Recheck();
            w.Show();
            return w;
        }

        PlaylistStudioSession S
        {
            get
            {
                if (session == null && !string.IsNullOrEmpty(contentId))
                {
                    session = PlaylistStudioSession.FromProject(contentId);
                    Recheck();
                }
                return session;
            }
        }

        PackagePlaylist Current => editingProposal?.playlist
            ?? (S != null && selected >= 0 && selected < S.data.playlists.Count ? S.data.playlists[selected] : null);

        void Recheck()
        {
            if (session == null) return;
            issues = session.Validate();
            if (editingProposal != null) session.Recheck(editingProposal);
            Repaint();
        }

        void Changed()
        {
            if (editingProposal == null && session != null) session.dirty = true;
            Recheck();
        }

        // ───────────────────────────── layout ─────────────────────────────

        void OnGUI()
        {
            DrawToolbar();
            if (S == null) { EditorGUILayout.HelpBox("Pick a title (Assets/Content/{id}) to start.", MessageType.Info); return; }
            if (S.loadError != null)
                EditorGUILayout.HelpBox("The playlists file could not be read: " + S.loadError + "\nFix it by hand (Open file), then Reload.", MessageType.Error);
            DrawTip();
            EditorGUILayout.BeginHorizontal();
            // The library sits beside the editor only: the simple Playlists view stays uncluttered.
            if (tab == EditTab) DrawLibrary(GUILayout.Width(Mathf.Clamp(position.width * 0.28f, 260f, 360f)));
            EditorGUILayout.BeginVertical();
            switch (tab)
            {
                case HomeTab: DrawHomeTab(); break;
                case EditTab: DrawEditTab(); break;
                case CampaignTab: DrawCampaignTab(); break;
                case GenerateTab: DrawGenerateTab(); break;
                default: DrawScriptTab(); break;
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var titles = PlaylistStudioHeadless.Titles();
            if (!string.IsNullOrEmpty(contentId) && !titles.Contains(contentId)) titles.Insert(0, contentId);
            int at = Mathf.Max(0, titles.IndexOf(contentId));
            int pick = titles.Count > 0 ? EditorGUILayout.Popup(at, titles.ToArray(), EditorStyles.toolbarPopup, GUILayout.Width(170f)) : -1;
            if (pick >= 0 && titles[pick] != contentId) { contentId = titles[pick]; session = null; selected = -1; editingProposal = null; }
            GUILayout.Space(8f);
            tab = GUILayout.Toolbar(tab, Tabs, EditorStyles.toolbarButton, GUILayout.Width(560f));
            GUILayout.FlexibleSpace();
            if (session != null)
            {
                int errors = issues.Count(i => i.isError);
                GUILayout.Label(errors > 0 ? $"✕ {errors} error(s)" : "✓ valid", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(!session.dirty))
                {
                    if (GUILayout.Button("Revert", EditorStyles.toolbarButton)) { session.ReloadData(); editingProposal = null; Recheck(); }
                    if (GUILayout.Button(session.dirty ? "Save*" : "Save", EditorStyles.toolbarButton, GUILayout.Width(52f))) { session.Save(); status = "Saved " + ContentPlaylistStore.AssetPath(contentId); }
                }
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton)) { session = null; editingProposal = null; fitCache.Clear(); }
                DrawBackendButtons();
                if (ContentPlaylistStore.Exists(contentId) && GUILayout.Button("Open file", EditorStyles.toolbarButton))
                    EditorUtility.OpenWithDefaultApp(ContentPlaylistStore.FullPath(contentId));
            }
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.LabelField(status, EditorStyles.miniLabel);
        }

        // ───────────────────────────── library ─────────────────────────────

        void DrawLibrary(GUILayoutOption width)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, width);
            GUILayout.Label($"Library · {S.library.Count} attractions", EditorStyles.boldLabel);
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            EditorGUILayout.BeginHorizontal();
            roleFilter = EditorGUILayout.Popup(roleFilter, RoleFilters, GUILayout.Width(110f));
            smallRoomOnly = GUILayout.Toggle(smallRoomOnly, "fits 12×18", EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();
            if (!S.hasCatalog)
            {
                EditorGUILayout.HelpBox("No challenge catalog (.dreampark-challenges.json): challenges are guessed from scripts.", MessageType.None);
                if (GUILayout.Button("Import challenge catalog…", EditorStyles.miniButton)) ImportCatalog();
            }
            // with an attraction selected, the goal-block panel below gets the lower half
            libScroll = string.IsNullOrEmpty(libSelected) ? EditorGUILayout.BeginScrollView(libScroll)
                : EditorGUILayout.BeginScrollView(libScroll, GUILayout.Height(Mathf.Max(160f, position.height * 0.42f)));
            foreach (PlaylistLibraryItem item in S.library.Where(Matches))
                DrawLibraryCard(item);
            EditorGUILayout.EndScrollView();
            DrawGoalPanel();
            GUILayout.Label("Drag a card onto a leg. Previews: Assets/Content/{id}/Previews.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        bool Matches(PlaylistLibraryItem i)
        {
            if (roleFilter > 0 && i.roleHint != RoleFilters[roleFilter]) return false;
            if (smallRoomOnly && !i.FitsSmallRoom) return false;
            if (string.IsNullOrWhiteSpace(search)) return true;
            string q = search.Trim().ToLowerInvariant();
            return i.leaf.ToLowerInvariant().Contains(q) || i.Name.ToLowerInvariant().Contains(q)
                || i.challenges.Any(c => c.Contains(q) || PlaylistChallenges.Name(c).ToLowerInvariant().Contains(q)) || i.objectives.Any(o => o.Contains(q));
        }

        void DrawLibraryCard(PlaylistLibraryItem item)
        {
            Rect r = GUILayoutUtility.GetRect(0f, 70f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, item.leaf == libSelected ? new Color(0.45f, 0.4f, 0.75f, 0.25f) : new Color(1f, 1f, 1f, 0.04f));
            Texture2D preview = PlaylistStudioDraw.Preview(item.previewPath);
            Rect img = new Rect(r.x + 3f, r.y + 3f, 64f, 64f);
            if (preview != null) GUI.DrawTexture(img, preview, ScaleMode.ScaleAndCrop);
            else EditorGUI.DrawRect(img, new Color(0.2f, 0.2f, 0.25f));
            float x = img.xMax + 6f;
            GUI.Label(new Rect(x, r.y + 2f, r.xMax - x, 16f), item.Name + (item.IsBossy ? "  ★" : ""), EditorStyles.boldLabel);
            string ladder = item.ladder.Count == 0 ? "no packing bake" : item.ladder.Count == 1 ? Size(item.authored)
                : $"{Size(item.authored)} → {Size(item.Minimum)}  ({item.ladder.Count} sizes)";
            GUI.Label(new Rect(x, r.y + 17f, r.xMax - x, 14f), $"{item.leaf} · {item.roleHint} · {ladder}", EditorStyles.miniLabel);
            float cx = x, cy = r.y + 34f;
            var chips = item.challenges.Count > 0 ? item.challenges.Select(c => (PlaylistChallenges.Name(c), Level(item, c)))
                : item.objectives.Select(o => (o.Replace('_', ' '), PlaylistStudioDraw.SkipColor));
            int goalCount = PlaylistGoalStore.KindsFor(S.goals, item.leaf).Count;
            if (goalCount > 0) chips = chips.Append(($"◆ {goalCount} goal{(goalCount == 1 ? "" : "s")}", PlaylistStudioDraw.Gold));
            foreach (var (text, color) in chips)
            {
                float w = PlaylistStudioDraw.ChipWidth(text);
                if (cx + w > r.xMax) { cx = x; cy += 16f; if (cy > r.yMax - 14f) break; }
                PlaylistStudioDraw.Chip(new Rect(cx, cy, w, 14f), text, color);
                cx += w + 3f;
            }
            if (item.badges.Count > 0) GUI.Label(new Rect(r.xMax - 70f, r.y + 2f, 68f, 14f), $"🏅 {item.badges.Count}", EditorStyles.miniLabel);
            if (!item.FitsSmallRoom) GUI.Label(new Rect(r.xMax - 70f, r.yMax - 16f, 68f, 14f), "big only", EditorStyles.miniLabel);


            Event e = Event.current;
            if (e.type == EventType.MouseDrag && r.Contains(e.mousePosition))
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(DragKey, item.leaf);
                DragAndDrop.objectReferences = new UnityEngine.Object[0];
                DragAndDrop.StartDrag(item.Name);
                e.Use();
            }
            if (e.type == EventType.MouseUp && r.Contains(e.mousePosition)) SelectLibraryItem(item);
            if (e.type == EventType.MouseDown && e.clickCount == 2 && r.Contains(e.mousePosition) && !string.IsNullOrEmpty(item.assetPath))
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(item.assetPath));
        }

        static Color Level(PlaylistLibraryItem item, string kind)
            => item.challengeLevels.TryGetValue(kind, out var l) && l == "live" ? PlaylistStudioDraw.FitsColor : PlaylistStudioDraw.StreamsColor;

        static string Size(Vector2 v) => $"{v.x:0.#}×{v.y:0.#} m";

        void ImportCatalog()
        {
            string path = EditorUtility.OpenFilePanel("Challenge catalog (a .dreampark-challenges.json, or DreamPark's catalog.json)", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            var rows = PlaylistChallenges.ReadCatalog(System.IO.File.ReadAllText(path), contentId);
            if (rows.Count == 0) { status = "That file has no attractions for " + contentId + "."; return; }
            System.IO.File.WriteAllText(System.IO.Path.GetFullPath(PlaylistStudioSession.ChallengesPath(contentId)), PlaylistChallenges.WriteTitleFile(rows));
            S.ReloadLibrary(); Recheck();
            status = $"Imported {rows.Count} attractions into {PlaylistStudioSession.ChallengesPath(contentId)}.";
        }

        // ───────────────────────────── first-run tip ─────────────────────────────

        void DrawTip()
        {
            if (EditorPrefs.GetBool(TipPref, false)) return;
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("💡  A playlist is one complete adventure: a start, a climax, an ending. Click a card to edit it, drag attractions "
                + "from the Library into its legs, or press Generate playlists and pick the ones you like. Everything else is under Advanced.",
                EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Got it", GUILayout.Width(60f))) EditorPrefs.SetBool(TipPref, true);
            EditorGUILayout.EndHorizontal();
        }

        // ───────────────────────────── simple view: the cards ─────────────────────────────

        void DrawHomeTab()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(generating ? "Generating… (a few minutes)" : "✨  Generate playlists", GUILayout.Height(40f)) && !generating)
            { tab = GenerateTab; Generate(null, null); }
            if (GUILayout.Button("+  New playlist", GUILayout.Height(40f), GUILayout.Width(220f)))
            { S.data.playlists.Add(NewPlaylist()); selected = S.data.playlists.Count - 1; editingProposal = null; tab = EditTab; Changed(); }
            EditorGUILayout.EndHorizontal();
            if (S.data.playlists.Count == 0)
            {
                EditorGUILayout.HelpBox("No playlists yet. Generate a complete set, start one from scratch, or bring your old organizers over.", MessageType.Info);
                if (GUILayout.Button("Convert my Adventure / Sequence / Arena organizers", GUILayout.Height(24f))) ConvertOrganizersNow();
                return;
            }
            GUILayout.Label("Your playlists. The first is the title's headline; right-click a card to move, duplicate or delete it.", EditorStyles.miniLabel);
            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            const float cw = 168f, ch = 252f, gap = 12f;     // 2:3 cards, like the app's Play shelf
            float avail = position.width - 30f;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((avail + gap) / (cw + gap)));
            int rows = Mathf.CeilToInt(S.data.playlists.Count / (float)perRow);
            Rect area = GUILayoutUtility.GetRect(avail, rows * (ch + gap));
            for (int i = 0; i < S.data.playlists.Count; i++)
                DrawCard(new Rect(area.x + (i % perRow) * (cw + gap), area.y + (i / perRow) * (ch + gap), cw, ch), i);
            EditorGUILayout.EndScrollView();
        }

        void DrawCard(Rect r, int index)
        {
            PackagePlaylist p = S.data.playlists[index];
            bool ok = !issues.Any(x => x.isError && x.playlistId == p.id);
            string kind = S.Kinds.FirstOrDefault(k => k.id == p.kind)?.singular ?? p.kind;
            var climax = p.Climax();
            PlaylistStop hero = climax?.stops.LastOrDefault(st => st.role == "boss" || st.role == "miniboss") ?? climax?.stops.LastOrDefault() ?? p.arc.SelectMany(l => l.stops).FirstOrDefault();
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, new Color(0.16f, 0.16f, 0.19f));
                Texture2D art = PlaylistStudioDraw.Preview(S.Item(hero?.attraction)?.previewPath);
                Rect artRect = new Rect(r.x, r.y, r.width, r.height * 0.5f);
                if (art != null) GUI.DrawTexture(artRect, art, ScaleMode.ScaleAndCrop);
                else EditorGUI.DrawRect(artRect, new Color(0.24f, 0.22f, 0.32f));
                EditorGUI.DrawRect(new Rect(r.x, artRect.yMax - 3f, r.width, 3f), PlaylistStudioDraw.BeatColor("climax"));
                PlaylistStudioDraw.DrawOutline(r, index == selected ? PlaylistStudioDraw.Violet : new Color(1f, 1f, 1f, 0.12f));
            }
            PlaylistStudioDraw.Chip(new Rect(r.x + 6f, r.y + 6f, PlaylistStudioDraw.ChipWidth(kind), 15f), kind, PlaylistStudioDraw.Violet);
            string status = (ok ? "✓" : "✕ fix") + (PlaylistGates.IsStarter(p) ? "" : " 🔒");
            PlaylistStudioDraw.Chip(new Rect(r.xMax - 6f - PlaylistStudioDraw.ChipWidth(status), r.y + 6f, PlaylistStudioDraw.ChipWidth(status), 15f), status,
                ok ? PlaylistStudioDraw.FitsColor : PlaylistStudioDraw.NoFitColor);
            float y = r.y + r.height * 0.5f + 4f;
            GUI.Label(new Rect(r.x + 8f, y, r.width - 16f, 34f), p.name, CardTitle);
            GUI.Label(new Rect(r.x + 8f, y + 34f, r.width - 16f, 16f), $"{p.arc.Sum(l => l.minutes)} min · {p.arc.Sum(l => l.stops.Count)} stops", EditorStyles.miniLabel);
            PlaylistStudioDraw.Arc(new Rect(r.x + 2f, y + 52f, r.width - 4f, 22f), p, false);
            if (hero != null)
                GUI.Label(new Rect(r.x + 8f, y + 74f, r.width - 16f, 16f), "★ " + (!string.IsNullOrEmpty(hero.displayName) ? hero.displayName : S.Item(hero.attraction)?.Name ?? PlaylistLibrary.Pretty(hero.attraction)), EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(r.x + 8f, y + 90f, r.width - 16f, 30f), p.pitch ?? "", CardPitch);

            Event e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                selected = index; editingProposal = null;
                if (e.button == 1)
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Edit"), false, () => { selected = index; tab = EditTab; });
                    menu.AddItem(new GUIContent("Duplicate"), false, () => Duplicate(index));
                    if (index > 0) menu.AddItem(new GUIContent("Move earlier"), false, () => { Swap(S.data.playlists, index, index - 1); selected = index - 1; Changed(); });
                    if (index < S.data.playlists.Count - 1) menu.AddItem(new GUIContent("Move later"), false, () => { Swap(S.data.playlists, index, index + 1); selected = index + 1; Changed(); });
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Delete…"), false, () => Delete(index));
                    menu.ShowAsContext();
                }
                else tab = EditTab;
                e.Use();
            }
        }

        static GUIStyle _cardTitle, _cardPitch;
        static GUIStyle CardTitle => _cardTitle ??= new GUIStyle(EditorStyles.boldLabel) { wordWrap = true, fontSize = 13 };
        static GUIStyle CardPitch => _cardPitch ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel) { clipping = TextClipping.Clip };

        void Duplicate(int index)
        {
            var copy = PlaylistStudioJson.Clone(S.data.playlists[index], contentId);
            copy.id = Unique(copy.id + "-copy"); copy.name += " (copy)";
            S.data.playlists.Insert(index + 1, copy); selected = index + 1; Changed();
        }

        void Delete(int index)
        {
            if (!EditorUtility.DisplayDialog("Delete playlist", $"Delete '{S.data.playlists[index].name}' from the working copy? (Revert brings it back until you Save.)", "Delete", "Keep")) return;
            S.data.playlists.RemoveAt(index); selected = Mathf.Min(index, S.data.playlists.Count - 1); Changed();
        }

        // ───────────────────────────── edit tab ─────────────────────────────

        void DrawEditTab()
        {
            DrawEditHeader();
            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            if (Current != null) DrawEditor(Current);
            else EditorGUILayout.HelpBox("Pick a playlist on the Playlists tab, or create one.", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        void DrawEditHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            if (editingProposal != null)
            {
                GUILayout.Label("Editing a generated proposal (not in your file yet)", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!editingProposal.CanSave))
                    if (GUILayout.Button("Accept into playlists", GUILayout.Width(150f))) { S.Accept(editingProposal); selected = S.data.playlists.Count - 1; editingProposal = null; Recheck(); }
                if (GUILayout.Button("Back", GUILayout.Width(60f))) { editingProposal = null; tab = GenerateTab; }
                EditorGUILayout.EndHorizontal();
                return;
            }
            if (GUILayout.Button("◀ All playlists", GUILayout.Width(110f))) tab = HomeTab;
            if (S.data.playlists.Count > 0)
            {
                int pick = EditorGUILayout.Popup(Mathf.Clamp(selected, 0, S.data.playlists.Count - 1), S.data.playlists.Select((x, i) => $"{i + 1}. {x.name}").ToArray());
                if (pick != selected) { selected = pick; GUI.FocusControl(null); }
            }
            if (GUILayout.Button("+ New", GUILayout.Width(60f))) { S.data.playlists.Add(NewPlaylist()); selected = S.data.playlists.Count - 1; Changed(); }
            using (new EditorGUI.DisabledScope(Current == null))
            {
                if (GUILayout.Button("Duplicate", GUILayout.Width(76f))) Duplicate(selected);
                if (GUILayout.Button("Delete", GUILayout.Width(60f))) Delete(selected);
            }
            EditorGUILayout.EndHorizontal();
        }

        static void Swap<T>(List<T> list, int a, int b) { T t = list[a]; list[a] = list[b]; list[b] = t; }

        string Unique(string id)
        {
            id = PlaylistProposals.Slug(id);
            string candidate = id; int n = 2;
            while (S.data.playlists.Any(p => p.id == candidate)) candidate = id + "-" + n++;
            return candidate;
        }

        PackagePlaylist NewPlaylist()
        {
            var p = new PackagePlaylist
            {
                id = Unique("new-playlist"), owner = "title", titleId = contentId, kind = S.Kinds[0].id, name = "New Playlist",
                pitch = "", fit = new PlaylistFit { minRooms = 1 }, arc = new List<PlaylistLeg>
                {
                    new PlaylistLeg { id = "leg1", name = "Opening", beat = "opening", minutes = 3, objective = new PlaylistObjective { type = "reach_exit" } },
                    new PlaylistLeg { id = "leg2", name = "Climax", beat = "climax", minutes = 5, objective = new PlaylistObjective { type = "defeat_boss" } },
                },
            };
            ContentPlaylistStore.Normalize(p);
            return p;
        }

        // ── the editor ──

        // Advanced sections start collapsed; the choice is remembered for this editor session.
        bool Fold(string key, string label, string summary = null)
        {
            bool open = SessionState.GetBool("DreamPark.PlaylistStudio.fold." + key, false);
            bool now = EditorGUILayout.Foldout(open, label + (string.IsNullOrEmpty(summary) || open ? "" : "   · " + summary), true);
            if (now != open) SessionState.SetBool("DreamPark.PlaylistStudio.fold." + key, now);
            return now;
        }

        void DrawEditor(PackagePlaylist p)
        {
            var mine = editingProposal != null ? editingProposal.issues : issues.Where(i => i.playlistId == p.id || (i.playlistId == null && i.isError)).ToList();
            EditorGUI.BeginChangeCheck();

            // ── the simple first screen: the arc, the name and kind, the legs and their stops ──
            Rect arc = GUILayoutUtility.GetRect(0f, 64f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(arc, new Color(0.08f, 0.08f, 0.1f));
            PlaylistStudioDraw.Arc(arc, p);
            GUILayout.Label($"{p.arc.Sum(l => l.minutes)} min · {p.arc.Count} legs · {p.arc.Sum(l => l.stops.Count)} stops · {PlaylistGates.Describe(p, PlaylistName, BadgeName)}", EditorStyles.centeredGreyMiniLabel);
            foreach (PlaylistIssue i in mine.Where(i => i.isError).Take(8))
                EditorGUILayout.HelpBox(i.message, MessageType.Error);
            int warnings = mine.Count(i => !i.isError);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            p.name = EditorGUILayout.TextField("Name", p.name);
            var kinds = S.Kinds;
            int k = Mathf.Max(0, kinds.FindIndex(x => x.id == p.kind));
            int nk = EditorGUILayout.Popup(k, kinds.Select(x => x.singular).Concat(new[] { "New kind word…" }).ToArray(), GUILayout.Width(130f));
            if (nk == kinds.Count) addingKind = true; else p.kind = kinds[nk].id;
            EditorGUILayout.EndHorizontal();
            if (addingKind)
            {
                EditorGUILayout.BeginHorizontal();
                newKindWord = EditorGUILayout.TextField(new GUIContent("New kind word", "Singular, in your title's voice: World, Mission, Cup, Party…"), newKindWord);
                if (GUILayout.Button("Add", GUILayout.Width(50f)) && !string.IsNullOrWhiteSpace(newKindWord)) { AddKind(p, newKindWord); addingKind = false; newKindWord = ""; }
                if (GUILayout.Button("Cancel", GUILayout.Width(60f))) addingKind = false;
                EditorGUILayout.EndHorizontal();
            }
            p.pitch = EditorGUILayout.TextField(new GUIContent("Pitch", "One sentence players and Dreamie hear"), p.pitch ?? "");
            // The playlist's word for its rounds (Oct 20): Race / World / Game / Match / Mission. Empty = Round / Rounds.
            EditorGUILayout.BeginHorizontal();
            string rwOne = EditorGUILayout.TextField(new GUIContent("Round word", "What players call a round of this playlist (≤ 16): Race, World, Game, Match, Mission. Empty = Round."), p.roundWord?.one ?? "");
            string rwMany = EditorGUILayout.TextField(new GUIContent("Plural", "Empty = the round word + s"), p.roundWord?.many ?? "");
            EditorGUILayout.EndHorizontal();
            if (rwOne.Length > PlaylistRoundWord.MaxChars) rwOne = rwOne.Substring(0, PlaylistRoundWord.MaxChars);
            if (rwMany.Length > PlaylistRoundWord.MaxChars) rwMany = rwMany.Substring(0, PlaylistRoundWord.MaxChars);
            if (rwOne != (p.roundWord?.one ?? "") || rwMany != (p.roundWord?.many ?? ""))
                p.roundWord = string.IsNullOrWhiteSpace(rwOne) && string.IsNullOrWhiteSpace(rwMany) ? null : new PlaylistRoundWord { one = rwOne, many = rwMany };
            EditorGUILayout.EndVertical();

            for (int i = 0; i < p.arc.Count; i++) DrawLeg(p, i);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add a leg", GUILayout.Height(22f)))
            {
                int climax = p.arc.FindIndex(l => l.beat == "climax");
                int at = climax >= 0 ? climax : p.arc.Count;
                p.arc.Insert(at, new PlaylistLeg { id = "leg" + (p.arc.Count + 1), name = "New leg", beat = at == 0 ? "opening" : "rising", minutes = 3,
                    objective = new PlaylistObjective { type = "reach_exit" } });
                ContentPlaylistStore.Normalize(p);
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();

            DrawFit(p);

            // ── Advanced (collapsed, sensible defaults) ──
            GUILayout.Space(6f);
            GUILayout.Label("Advanced" + (warnings > 0 ? $"   ({warnings} suggestion(s))" : ""), EditorStyles.boldLabel);
            if (warnings > 0 && Fold("warnings", "Suggestions", warnings + ""))
                foreach (PlaylistIssue i in mine.Where(i => !i.isError).Take(12)) EditorGUILayout.HelpBox(i.message, MessageType.Warning);
            bool lobby = p.arc.Count > 0 && IsBookendLeg(p.arc[0], PlaylistBookends.StartLobby);
            bool results = p.arc.Count > 0 && IsBookendLeg(p.arc[p.arc.Count - 1], PlaylistBookends.ResultsRoom);
            if (Fold("bookends", "Start & end (bookends)", (lobby ? "Start Lobby" : "starts in the game") + ", " + (results ? "Results Room" : "no results room")))
            {
                EditorGUILayout.BeginHorizontal();
                bool nl = GUILayout.Toggle(lobby, "Start Lobby (gather, 1 min)", EditorStyles.miniButtonLeft);
                bool nr = GUILayout.Toggle(results, "Results Room (ranked, 90 s)", EditorStyles.miniButtonRight);
                if (nl != lobby) SetBookend(p, PlaylistBookends.StartLobby, nl);
                if (nr != results) SetBookend(p, PlaylistBookends.ResultsRoom, nr);
                EditorGUILayout.EndHorizontal();
                GUILayout.Label("Gather players first for parties, cups and co-op; end in the Results Room when anyone is scored. Quick solo runs start straight in the first game.", EditorStyles.wordWrappedMiniLabel);
            }
            if (Fold("rules", "Rules & timers", p.rules.IsDefault ? "none" : $"{p.rules.advance} {p.rules.timerSec}s {p.rules.scoring} {p.rules.lives} lives"))
            {
                EditorGUILayout.BeginHorizontal();
                p.rules.advance = Popup(p.rules.advance, PackagePlaylist.Advances, 100f);
                p.rules.timerSec = EditorGUILayout.IntField(new GUIContent("timer s"), p.rules.timerSec, GUILayout.Width(180f));
                p.rules.scoring = Popup(p.rules.scoring, PackagePlaylist.Scorings, 90f, "(no scoring)");
                p.rules.lives = EditorGUILayout.IntField(new GUIContent("lives"), p.rules.lives, GUILayout.Width(180f));
                EditorGUILayout.EndHorizontal();
                GUILayout.Label("on_timer + a timer = a minute-challenge gauntlet; scoring sums or ranks across legs; lives end the run.", EditorStyles.wordWrappedMiniLabel);
            }
            if (Fold("fit", "Where it plays (fit hint)", PlaylistFitCheck.FitText(p.fit)))
            {
                EditorGUILayout.BeginHorizontal();
                p.fit.minRooms = EditorGUILayout.IntField("min rooms", p.fit.minRooms, GUILayout.Width(200f));
                p.fit.minAreaM2 = EditorGUILayout.FloatField("min m²", p.fit.minAreaM2, GUILayout.Width(200f));
                p.fit.indoor = GUILayout.Toggle(p.fit.indoor, "indoor", EditorStyles.miniButtonLeft);
                p.fit.outdoor = GUILayout.Toggle(p.fit.outdoor, "outdoor", EditorStyles.miniButtonRight);
                EditorGUILayout.EndHorizontal();
            }
            // Record v2 (CONTRACT § 8): how the group plays it + the title release it needs.
            p.play = p.play ?? new PlaylistPlay();
            string playLine = (string.IsNullOrEmpty(p.play.mode) ? "mode unset" : p.play.mode)
                + (p.play.players.Count == 2 ? $" · {p.play.Min}–{p.play.Max} players" : "") + (p.requiresRelease > 0 ? $" · needs release {p.requiresRelease}" : "");
            if (Fold("play", "Who plays (mode, players, release)", playLine))
            {
                EditorGUILayout.BeginHorizontal();
                p.play.mode = Popup(p.play.mode, PlaylistRecordV2.Modes, 90f, "(mode)");
                float lw0 = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 70f;
                int min = EditorGUILayout.IntField(new GUIContent("players", "The group size it is for: min – max (the picker filters by it)."), Mathf.Max(0, p.play.Min), GUILayout.Width(110f));
                int max = EditorGUILayout.IntField(Mathf.Max(0, p.play.Max), GUILayout.Width(36f));
                if (min > 0 || max > 0) p.play.players = new List<int> { Mathf.Max(1, min), Mathf.Max(Mathf.Max(1, min), max) };
                else p.play.players.Clear();
                EditorGUIUtility.labelWidth = 110f;
                p.requiresRelease = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("needs release", "The minimum title release this playlist needs (its attractions, events, goal blocks). 0 = any. Older installs hide it."), p.requiresRelease, GUILayout.Width(170f)));
                EditorGUIUtility.labelWidth = lw0;
                EditorGUILayout.EndHorizontal();
                GUILayout.Label("coop: Results show one team total + each player's part · versus / party: a ranking.", EditorStyles.wordWrappedMiniLabel);
            }
            if (Fold("gates", "Chapter & unlocks", PlaylistGates.Describe(p, PlaylistName, BadgeName) + (p.continueWith.Count > 0 ? ", then " + PlaylistName(p.continueWith[0]) : "")))
                DrawGates(p);
            if (Fold("details", "Id, tags & badges", p.id))
            {
                p.id = EditorGUILayout.TextField(new GUIContent("Id", "Stable forever: lower-case letters, digits, - and _."), p.id);
                p.tags = Csv("Tags", p.tags);
                DrawBadges(p);
            }
            if (EditorGUI.EndChangeCheck()) { ContentPlaylistStore.Normalize(p); fitCache.Remove(p); Changed(); }
        }

        string PlaylistName(string id) => S.data.playlists.FirstOrDefault(x => x.id == id)?.name ?? id;
        string BadgeName(string id) => S.badges.FirstOrDefault(b => b.id == id)?.name ?? id;

        void AddKind(PackagePlaylist p, string word)
        {
            word = word.Trim();
            var k = new PlaylistKind { id = PlaylistProposals.Slug(word), singular = word, plural = word.EndsWith("s") ? word : word + "s" };
            if (string.IsNullOrEmpty(k.id)) return;
            if (!S.data.kinds.Any(x => x.id == k.id))
            {
                if (S.data.kinds.Count == 0) S.data.kinds.AddRange(S.Kinds);   // keep the implicit default declared
                S.data.kinds.Add(k);
            }
            p.kind = k.id;
            GUI.changed = true;
        }

        void DrawBadges(PackagePlaylist p)
        {
            if (S.badges.Count == 0) { p.badges = Csv("Badges", p.badges); return; }
            var options = S.badges.Select(b => b.id).ToList();
            int mask = 0;
            for (int i = 0; i < options.Count && i < 31; i++) if (p.badges.Contains(options[i])) mask |= 1 << i;
            int nm = EditorGUILayout.MaskField("Badges", mask, S.badges.Take(31).Select(b => $"{b.name} ({b.id})").ToArray());
            if (nm != mask) p.badges = options.Where((id, i) => i < 31 && (nm & (1 << i)) != 0).Concat(p.badges.Where(b => options.IndexOf(b) >= 31 || !options.Contains(b))).Distinct().ToList();
        }

        void DrawGates(PackagePlaylist p)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            p.chapter = EditorGUILayout.TextField(new GUIContent("Chapter", "Optional label on the campaign map"), p.chapter ?? "");
            var others = S.data.playlists.Where(x => x != p && !string.IsNullOrEmpty(x.id)).Select(x => x.id).ToList();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Continue with", "The suggested next playlist(s), best first"));
            for (int i = 0; i < p.continueWith.Count; i++)
                if (GUILayout.Button(PlaylistName(p.continueWith[i]) + "  ✕", EditorStyles.miniButton)) { p.continueWith.RemoveAt(i); GUI.changed = true; break; }
            int add = EditorGUILayout.Popup(0, new[] { "+ add…" }.Concat(others.Where(o => !p.continueWith.Contains(o)).Select(PlaylistName)).ToArray(), GUILayout.Width(110f));
            if (add > 0) { p.continueWith.Add(others.Where(o => !p.continueWith.Contains(o)).ElementAt(add - 1)); GUI.changed = true; }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Unlocks when", "All must hold. Empty = always open (a starter)."));
            var gate = PlaylistGates.Requires(p);
            if (gate.Count == 0) GUILayout.Label("always open", EditorStyles.miniLabel);
            for (int i = 0; i < gate.Count; i++)
                if (GUILayout.Button(PlaylistGates.Words(gate[i], PlaylistName, BadgeName) + "  ✕", EditorStyles.miniButton))
                { gate.RemoveAt(i); PlaylistGates.SetRequires(p, gate); GUI.changed = true; break; }
            var menuItems = new List<(string label, string reference)>();
            menuItems.AddRange(others.Select(o => ("finished: " + PlaylistName(o), PlaylistGates.Ref(PlaylistGates.PlaylistRef, o))));
            menuItems.AddRange(S.badges.Select(b => ("badge: " + b.name, PlaylistGates.Ref(PlaylistGates.BadgeRef, b.id))));
            menuItems.AddRange(S.data.playlists.SelectMany(x => PlaylistGates.Sets(x).flags.Select(f => f.flag)).Distinct().Select(f => ("flag: " + f, PlaylistGates.Ref(PlaylistGates.FlagRef, f))));
            var have = new HashSet<string>(gate.Select(PlaylistGates.Encode));
            menuItems = menuItems.Where(m => !have.Contains(m.reference)).ToList();
            int r = EditorGUILayout.Popup(0, new[] { "+ add…" }.Concat(menuItems.Select(m => m.label)).ToArray(), GUILayout.Width(110f));
            if (r > 0) { gate.Add(PlaylistGates.Parse(menuItems[r - 1].reference)); PlaylistGates.SetRequires(p, gate); GUI.changed = true; }
            EditorGUILayout.EndHorizontal();
            var sets = PlaylistGates.Sets(p);
            var flags = sets.flags.Select(f => f.value == null ? f.flag : f.flag + "=" + f.value).ToList();
            var nf = Csv(new GUIContent("Sets flags", "Story flags a WON run sets: name, or name=value"), flags);
            var nb = Csv(new GUIContent("Awards badges", "Badges a WON run awards (sets.badges)"), sets.badges);
            if (!ReferenceEquals(nf, flags) || !ReferenceEquals(nb, sets.badges)) { PlaylistGates.SetSets(p, nf, nb); GUI.changed = true; }
            if (!string.IsNullOrEmpty(p.windowJson) || !string.IsNullOrEmpty(p.personalJson))
                GUILayout.Label("Also carried as authored: " + (string.IsNullOrEmpty(p.windowJson) ? "" : "window " + p.windowJson + " ") + (string.IsNullOrEmpty(p.personalJson) ? "" : "personal " + p.personalJson), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        string GateText(string reference)
        {
            if (!PlaylistGates.TryParse(reference, out string kind, out string id)) return reference;
            return kind == PlaylistGates.PlaylistRef ? "after " + PlaylistName(id) : kind == PlaylistGates.BadgeRef ? "badge " + BadgeName(id) : "flag " + id;
        }

        static bool IsBookendLeg(PlaylistLeg leg, string id) => leg.stops.Count == 1 && PlaylistBookends.IsPlatformStop(leg.stops[0].contentId, leg.stops[0].attraction) && PlaylistBookends.IdOf(leg.stops[0].attraction) == id;

        static void SetBookend(PackagePlaylist p, string id, bool on)
        {
            bool lobby = id == PlaylistBookends.StartLobby;
            if (!on) { p.arc.RemoveAll(l => IsBookendLeg(l, id)); GUI.changed = true; return; }
            var leg = new PlaylistLeg
            {
                id = lobby ? "lobby" : "results", name = lobby ? "Start Lobby" : "Results Room", beat = lobby ? "opening" : "finale",
                minutes = lobby ? 1 : 2, next = "walk_on", titles = new List<string> { PlaylistBookends.ContentId },
                objective = lobby ? new PlaylistObjective { type = "reach_exit", target = PlaylistBookends.StartLobby } : new PlaylistObjective { type = "time", seconds = 90 },
                stops = new List<PlaylistStop> { new PlaylistStop { contentId = PlaylistBookends.ContentId, attraction = id, role = lobby ? "start" : "end", placement = new PlaylistPlacement { prefer = "players" } } },
            };
            if (lobby) p.arc.Insert(0, leg); else p.arc.Add(leg);
            GUI.changed = true;
        }

        void DrawLeg(PackagePlaylist p, int index)
        {
            PlaylistLeg leg = p.arc[index];
            Color c = PlaylistStudioDraw.BeatColor(leg.beat);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            Rect bar = GUILayoutUtility.GetRect(0f, 3f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(bar, c);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Leg {index + 1}", EditorStyles.boldLabel, GUILayout.Width(44f));
            leg.name = EditorGUILayout.TextField(leg.name);
            leg.beat = Popup(leg.beat, PackagePlaylist.Beats, 80f);
            leg.minutes = EditorGUILayout.IntField(new GUIContent("", "Estimated minutes (cards + the queue). Hard caps live in the playlist's timing."), leg.minutes, GUILayout.Width(34f)); GUILayout.Label("est min", GUILayout.Width(44f));
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f))) { p.arc.RemoveAt(index); GUI.changed = true; EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical(); return; }
            EditorGUILayout.EndHorizontal();

            for (int j = 0; j < leg.stops.Count; j++)
                if (DrawStop(leg, j)) break;

            Rect drop = GUILayoutUtility.GetRect(0f, 24f, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "⤓ Drop an attraction here", EditorStyles.helpBox);
            HandleDrop(drop, leg, null);

            string ends = PlaylistObjectiveWords(leg);
            if (Fold($"leg.{p.id}.{index}", "More: how it ends, what comes next, goal line", ends))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Ends when", GUILayout.Width(66f));
                leg.objective.type = Popup(leg.objective.type, PackagePlaylist.Objectives, 100f);
                var targets = new[] { "(any stop)" }.Concat(leg.stops.Select(st => st.attraction)).ToArray();
                int t = Mathf.Max(0, Array.IndexOf(targets, leg.objective.target));
                int nt = EditorGUILayout.Popup(t, targets, GUILayout.Width(150f));
                leg.objective.target = nt == 0 ? null : targets[nt];
                float lw = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 56f;
                if (leg.objective.type == "score" || leg.objective.type == "collect") leg.objective.count = EditorGUILayout.IntField("count", leg.objective.count, GUILayout.Width(130f));
                if (leg.objective.type == "survive" || leg.objective.type == "time") leg.objective.seconds = EditorGUILayout.IntField("seconds", leg.objective.seconds, GUILayout.Width(130f));
                EditorGUIUtility.labelWidth = lw;
                // Record v2: a round's end is DERIVED (a boss round ends on its boss, any other when every stop is over);
                // only a timed / scored end the derivation can't make is saved.
                GUILayout.Label(PlaylistStudioJson.WritesObjective(leg) ? "saved (timed / scored round)" : "derived, not saved", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                if (index < p.arc.Count - 1) { GUILayout.Label("Then", GUILayout.Width(66f)); leg.next = Popup(leg.next, PackagePlaylist.Transitions, 110f); }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("▲ move up", EditorStyles.miniButtonLeft, GUILayout.Width(80f)) && index > 0) { Swap(p.arc, index, index - 1); GUI.changed = true; }
                if (GUILayout.Button("▼ move down", EditorStyles.miniButtonRight, GUILayout.Width(90f)) && index < p.arc.Count - 1) { Swap(p.arc, index, index + 1); GUI.changed = true; }
                EditorGUILayout.EndHorizontal();
                leg.objectiveText = EditorGUILayout.TextField(new GUIContent("Goal line", "Optional (≤ 120). Empty = generated: \"Defeat King Jubbo!\""), leg.objectiveText);
                DreamieText(leg, "guide", "Dreamie (round)", "What Dreamie says about this round as it appears (her guidance, not exact words).");
            }
            EditorGUILayout.EndVertical();
        }

        /// A round's / stop's colocated Dreamie guidance (record v2 § 8.5: `dreamie {guide, intro, react}`), kept as the
        /// object's `dreamie` extra so every other key (react, must, maxSec) passes through a save untouched.
        static void DreamieText(object model, string key, string label, string tip)
        {
            JSONObject d = PlaylistStudioJson.Extra(model, "dreamie");
            string was = d?.GetField(key)?.stringValue ?? "";
            string now = EditorGUILayout.TextField(new GUIContent(label, tip), was);
            if (now == was) return;
            JSONObject c = d != null && d.type == JSONObject.Type.Object ? PlaylistStudioJson.DeepCopy(d) : JSONObject.emptyObject;
            if (string.IsNullOrWhiteSpace(now)) c.RemoveField(key); else c.SetField(key, now);
            PlaylistStudioJson.SetExtra(model, "dreamie", c.count > 0 ? c : null);
            GUI.changed = true;
        }

        string PlaylistObjectiveWords(PlaylistLeg leg)
        {
            var o = leg.objective ?? new PlaylistObjective();
            string target = string.IsNullOrEmpty(o.target) ? "" : " " + (S.Item(o.target)?.Name ?? PlaylistBookends.DisplayName(o.target) ?? o.target);
            switch (o.type)
            {
                case "defeat_boss": return "ends when the boss falls" + (target.Length > 0 ? ":" + target : "");
                case "score": return $"ends at {o.count} points";
                case "collect": return $"ends after collecting {o.count}";
                case "survive": return $"ends after surviving {o.seconds} s";
                case "time": return $"ends after {o.seconds} s";
                default: return "ends when the stops are cleared" + (target.Length > 0 ? ":" + target : "");
            }
        }

        /// True when the stop list changed (stop iterating).
        bool DrawStop(PlaylistLeg leg, int j)
        {
            PlaylistStop s = leg.stops[j];
            bool bookend = PlaylistBookends.IsPlatformStop(s.contentId, s.attraction);
            PlaylistLibraryItem item = bookend ? null : S.Item(s.attraction);
            EditorGUILayout.BeginHorizontal();
            Rect img = GUILayoutUtility.GetRect(36f, 36f, GUILayout.Width(36f));
            Texture2D preview = PlaylistStudioDraw.Preview(item?.previewPath);
            if (preview != null) GUI.DrawTexture(img, preview, ScaleMode.ScaleAndCrop);
            else EditorGUI.DrawRect(img, bookend ? new Color(0.35f, 0.3f, 0.5f) : item == null ? new Color(0.5f, 0.15f, 0.15f) : new Color(0.2f, 0.2f, 0.25f));
            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal();
            string name = bookend ? PlaylistBookends.DisplayName(s.attraction) : item?.Name ?? s.attraction + " (missing!)";
            GUILayout.Label((s.role == "boss" || s.role == "miniboss" ? "★ " : "") + name, EditorStyles.boldLabel, GUILayout.Width(180f));
            s.role = Popup(s.role, PackagePlaylist.Roles, 80f);
            string derived = PlaylistChallenges.Derive(s.role, leg.objective.type, item?.defaultChallenge);
            if (!bookend) GUILayout.Label(PlaylistChallenges.Name(string.IsNullOrEmpty(s.challenge) ? derived : s.challenge) + (s.bonus.Count > 0 ? " + bonus" : ""), EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            string key = $"stop.{leg.id}.{j}";
            bool open = SessionState.GetBool("DreamPark.PlaylistStudio.fold." + key, false);
            if (GUILayout.Toggle(open, new GUIContent("⋯", "Advanced: placement, challenge, bonus, names, options, smaller alternates"), EditorStyles.miniButton, GUILayout.Width(26f)) != open)
                SessionState.SetBool("DreamPark.PlaylistStudio.fold." + key, !open);
            bool removed = GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f));
            EditorGUILayout.EndHorizontal();
            if (SessionState.GetBool("DreamPark.PlaylistStudio.fold." + key, false))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("placed", EditorStyles.miniLabel, GUILayout.Width(40f));
                s.placement.prefer = Popup(s.placement.prefer, PackagePlaylist.Placements, 80f);
                if (!bookend)
                {
                    var kinds = PlaylistChallenges.Kinds.Where(k => k.type != "modifier").Select(k => k.id).ToArray();
                    GUILayout.Label("challenge", EditorStyles.miniLabel, GUILayout.Width(56f));
                    s.challenge = Popup(s.challenge, kinds, 110f, "auto: " + derived);
                    GUILayout.Label("bonus", EditorStyles.miniLabel, GUILayout.Width(36f));
                    foreach (var m in PlaylistChallenges.Kinds.Where(k => k.type == "modifier"))
                    {
                        bool on = s.bonus.Contains(m.id);
                        if (GUILayout.Toggle(on, new GUIContent(m.name, "Bonus line, never required"), EditorStyles.miniButton) != on)
                        { if (on) s.bonus.Remove(m.id); else s.bonus.Add(m.id); GUI.changed = true; }
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                s.displayName = EditorGUILayout.TextField(s.displayName ?? "", GUILayout.Width(170f));
                if (string.IsNullOrEmpty(s.displayName) && Event.current.type == EventType.Repaint)
                    GUI.Label(GUILayoutUtility.GetLastRect(), " display name", EditorStyles.centeredGreyMiniLabel);
                s.objectiveText = EditorGUILayout.TextField(s.objectiveText ?? "");
                if (string.IsNullOrEmpty(s.objectiveText) && Event.current.type == EventType.Repaint)
                    GUI.Label(GUILayoutUtility.GetLastRect(), " goal line (auto)", EditorStyles.centeredGreyMiniLabel);
                s.options = EditorGUILayout.TextField(s.options ?? "", GUILayout.Width(150f));
                if (string.IsNullOrEmpty(s.options) && Event.current.type == EventType.Repaint)
                    GUI.Label(GUILayoutUtility.GetLastRect(), " options {…}", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndHorizontal();
                if (!bookend && s.lockedUntil != null && s.lockedUntil.Count > 0)
                    s.lockSign = EditorGUILayout.TextField(new GUIContent("Lock sign", "The force field's words while it waits for " + string.Join(", ", s.lockedUntil)
                        + " (≤ 120). Empty = \"Finish … to unlock\". The lock itself is native: any result of those stops opens it."), s.lockSign ?? "");
                if (!bookend)
                {
                    DreamieText(s, "intro", "Dreamie intro", "Her guidance as players arrive at this stop.");
                    DreamieText(s, "guide", "Dreamie (stop)", "Anything else she should know about this stop.");
                }
                if (!bookend)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Smaller alternates:", EditorStyles.miniLabel, GUILayout.Width(100f));
                    for (int a = 0; a < s.alternates.Count; a++)
                        if (GUILayout.Button((S.Item(s.alternates[a])?.Name ?? s.alternates[a]) + " ✕", EditorStyles.miniButton)) { s.alternates.RemoveAt(a); GUI.changed = true; break; }
                    Rect altDrop = GUILayoutUtility.GetRect(90f, 16f, GUILayout.Width(90f));
                    GUI.Label(altDrop, "⤓ drop", EditorStyles.centeredGreyMiniLabel);
                    HandleDrop(altDrop, leg, s);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            if (removed) { leg.stops.RemoveAt(j); GUI.changed = true; return true; }
            return false;
        }

        void HandleDrop(Rect r, PlaylistLeg leg, PlaylistStop alternateOf)
        {
            Event e = Event.current;
            if (!r.Contains(e.mousePosition) || !(DragAndDrop.GetGenericData(DragKey) is string leaf)) return;
            if (e.type == EventType.DragUpdated) { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.Use(); }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                PlaylistLibraryItem item = S.Item(leaf);
                if (alternateOf != null) { if (!alternateOf.alternates.Contains(leaf) && alternateOf.attraction != leaf) alternateOf.alternates.Add(leaf); }
                else
                {
                    string role = item != null && item.IsBossy ? item.roleHint : leg.stops.Count == 0 && leg.beat == "opening" ? "start" : item?.roleHint == "end" ? "end" : "stage";
                    leg.stops.Add(new PlaylistStop
                    {
                        attraction = leaf, role = role, alternates = new List<string>(), bonus = new List<string>(),
                        placement = new PlaylistPlacement { prefer = role == "boss" ? "biggest" : role == "start" ? "entrance" : "any" },
                    });
                    // Sensible default: a boss dropped into a leg that still ends on "clear the stops" makes it a boss fight.
                    if ((role == "boss" || role == "miniboss") && (leg.objective.type == "defeat_boss" || leg.objective.type == "reach_exit"))
                    { leg.objective.type = "defeat_boss"; leg.objective.target = leaf; }
                }
                GUI.changed = true;
                e.Use();
            }
        }

        // ── fit ──

        void DrawFit(PackagePlaylist p)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            string hash = PlaylistStudioJson.WritePlaylist(p, contentId).Print();
            if (!fitCache.ContainsKey(p) && hash != fitHash)
            {
                fitHash = hash;
                try { fitCache[p] = S.Fit(p); } catch (Exception ex) { status = "Fit check failed: " + ex.Message; fitCache[p] = new List<PlaylistFitRow>(); }
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Does it fit?", EditorStyles.boldLabel, GUILayout.Width(80f));
            Rect chips = GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(new GUIContent("↻", "Run the fit check again"), EditorStyles.miniButton, GUILayout.Width(24f))) { fitCache.Remove(p); fitHash = null; }
            EditorGUILayout.EndHorizontal();
            if (fitCache.TryGetValue(p, out var rows) && rows.Count > 0)
            {
                float x = chips.x;
                foreach (PlaylistFitRow row in rows)
                {
                    string text = row.space.label + " " + (row.verdict == PlaylistFitCheck.Fits ? "✓" : row.verdict == PlaylistFitCheck.Streams ? "≈" : row.verdict == PlaylistFitCheck.Skipped ? "–" : "✕");
                    float w = PlaylistStudioDraw.ChipWidth(text);
                    PlaylistStudioDraw.Chip(new Rect(x, chips.y, w, 15f), text, PlaylistStudioDraw.VerdictColor(row.verdict));
                    x += w + 3f;
                }
                if (!PlaylistFitCheck.Passes(rows, out string why)) EditorGUILayout.HelpBox(why, MessageType.Error);
                if (Fold("fitplans", "Show the plans (the real packer, top-down: ✓ fits · ≈ streams one after another · ✕ no floor)"))
                {
                    float avail = position.width - Mathf.Clamp(position.width * 0.28f, 260f, 360f) - 60f;
                    float cellW = avail / rows.Count;
                    Rect strip = GUILayoutUtility.GetRect(0f, cellW * 0.8f + 34f, GUILayout.ExpandWidth(true));
                    for (int i = 0; i < rows.Count; i++)
                    {
                        PlaylistFitRow row = rows[i];
                        Rect cell = new Rect(strip.x + i * (strip.width / rows.Count) + 2f, strip.y, strip.width / rows.Count - 4f, strip.height);
                        PlaylistStudioDraw.Chip(new Rect(cell.x, cell.y, cell.width, 15f), $"{row.space.label}: {row.verdict}", PlaylistStudioDraw.VerdictColor(row.verdict));
                        PlaylistStudioDraw.Thumbnail(new Rect(cell.x, cell.y + 17f, cell.width, cell.height - 34f), row);
                        GUI.Label(new Rect(cell.x, cell.yMax - 16f, cell.width, 16f), new GUIContent(row.reason ?? (row.layoutMode ?? ""), string.Join("\n", row.notes)), EditorStyles.miniLabel);
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        // ───────────────────────────── script tab (PlaylistStudioWindow.Scripting.cs) ─────────────────────────────

        void DrawScriptTab() => DrawScriptingTab();

        // ───────────────────────────── campaign tab ─────────────────────────────

        void DrawCampaignTab()
        {
            var nodes = PlaylistGates.Map(S.data, out var edges);
            EditorGUILayout.HelpBox("Chapters & unlocks: green cards are always open (the starter set), gold cards are locked until their rule holds. "
                + "Columns follow the chain. Solid arrows unlock (requires: finish / badge / flag), dashed arrows suggest what comes next (continueWith). Click a card to edit it.", MessageType.None);
            foreach (PlaylistIssue i in issues.Where(i => i.message.Contains("requires") || i.message.Contains("continueWith") || i.message.Contains("locked") || i.message.Contains("flag")).Take(8))
                EditorGUILayout.HelpBox((i.playlistId != null ? i.playlistId + ": " : "") + i.message, i.isError ? MessageType.Error : MessageType.Warning);
            if (nodes.Count == 0) { GUILayout.Label("No playlists yet."); return; }
            DrawCampaignLuaPanel();
            int cols = nodes.Max(n => n.column) + 1, rows = nodes.Max(n => n.row) + 1;
            const float cw = 190f, ch = 84f, gx = 46f, gy = 22f;
            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            Rect area = GUILayoutUtility.GetRect(cols * (cw + gx) + 20f, rows * (ch + gy) + 30f);
            Rect Box(PlaylistGates.MapNode n) => new Rect(area.x + 10f + n.column * (cw + gx), area.y + 24f + n.row * (ch + gy), cw, ch);
            var byId = nodes.ToDictionary(n => n.playlist.id);
            for (int c = 0; c < cols; c++)
                GUI.Label(new Rect(area.x + 10f + c * (cw + gx), area.y, cw, 18f), c == 0 ? "First" : "Then (" + c + ")", EditorStyles.centeredGreyMiniLabel);
            Handles.BeginGUI();
            foreach (var e in edges)
            {
                if (!byId.TryGetValue(e.from, out var a) || !byId.TryGetValue(e.to, out var b)) continue;
                Rect ra = Box(a), rb = Box(b);
                Vector3 p0 = new Vector3(ra.xMax, ra.center.y), p1 = new Vector3(rb.x, rb.center.y);
                if (b.column <= a.column) { p0 = new Vector3(ra.center.x, ra.yMax); p1 = new Vector3(rb.center.x, rb.y); }
                Handles.color = e.unlock ? PlaylistStudioDraw.Gold : new Color(1f, 1f, 1f, 0.55f);
                if (e.unlock) Handles.DrawAAPolyLine(3f, p0, p1); else Handles.DrawDottedLine(p0, p1, 4f);
                Vector3 dir = (p1 - p0).normalized, side = new Vector3(-dir.y, dir.x);
                Handles.DrawAAConvexPolygon(p1, p1 - dir * 9f + side * 5f, p1 - dir * 9f - side * 5f);
            }
            Handles.EndGUI();
            foreach (var n in nodes)
            {
                Rect r = Box(n);
                bool sel = S.data.playlists.IndexOf(n.playlist) == selected;
                EditorGUI.DrawRect(r, sel ? new Color(0.3f, 0.27f, 0.45f) : new Color(0.18f, 0.18f, 0.21f));
                PlaylistStudioDraw.DrawOutline(r, n.starter ? PlaylistStudioDraw.FitsColor : PlaylistStudioDraw.Gold);
                GUI.Label(new Rect(r.x + 6f, r.y + 3f, r.width - 12f, 16f), (n.starter ? "" : "🔒 ") + n.playlist.name, EditorStyles.boldLabel);
                if (!string.IsNullOrEmpty(n.playlist.chapter)) GUI.Label(new Rect(r.x + 6f, r.y + 19f, r.width - 12f, 14f), n.playlist.chapter, EditorStyles.miniLabel);
                PlaylistStudioDraw.Arc(new Rect(r.x + 2f, r.y + 36f, r.width - 4f, 24f), n.playlist, false);
                string luaLine = LuaCampaignLine(n.playlist);
                GUI.Label(new Rect(r.x + 6f, r.yMax - 18f, r.width - 12f, 16f), new GUIContent(PlaylistGates.Describe(n.playlist, PlaylistName, BadgeName)
                    + (PlaylistGates.SetFlags(n.playlist).Any() ? " · sets " + string.Join(",", PlaylistGates.SetFlags(n.playlist)) : "")
                    + (luaLine.Length > 0 ? " · ƒ" : ""), luaLine), EditorStyles.miniLabel);
                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
                { selected = S.data.playlists.IndexOf(n.playlist); editingProposal = null; tab = EditTab; Event.current.Use(); }
            }
            EditorGUILayout.EndScrollView();
        }

        // ───────────────────────────── generate tab ─────────────────────────────

        void DrawGenerateTab()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Generate playlists for this game", EditorStyles.boldLabel);
            GUILayout.Label("Claude (Opus 5.5, via DreamPark) reads this title's attractions, challenges, badges, scripts and READMEs and proposes complete playlists: "
                + "an arc with a climax and an ending, varied challenges, your title's own words, sized for a 12×18 ft room. Every proposal is repaired, "
                + "validated and fit-checked here before you can accept it. Nothing is saved until you accept and Save.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            generateCount = EditorGUILayout.IntSlider("How many", generateCount, 5, 15);
            using (new EditorGUI.DisabledScope(generating))
                if (GUILayout.Button(generating ? "Generating… (a few minutes)" : "✨ Generate", GUILayout.Width(200f), GUILayout.Height(24f))) Generate(null, null);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Remix", GUILayout.Width(44f));
            using (new EditorGUI.DisabledScope(generating || Current == null || editingProposal != null))
            {
                GUILayout.Label(Current != null ? "'" + Current.name + "' →" : "(select a playlist)", EditorStyles.miniLabel, GUILayout.Width(170f));
                if (GUILayout.Button("Jump Gauntlet", EditorStyles.miniButtonLeft)) Generate(Current, "jump-gauntlet");
                if (GUILayout.Button("Boss Rush", EditorStyles.miniButtonMid)) Generate(Current, "boss-rush");
                if (GUILayout.Button("Speed Round", EditorStyles.miniButtonRight)) Generate(Current, "speed-round");
                remixVariant = EditorGUILayout.TextField(remixVariant, GUILayout.MinWidth(120f));
                if (GUILayout.Button("Remix", EditorStyles.miniButton, GUILayout.Width(52f)) && !string.IsNullOrWhiteSpace(remixVariant)) Generate(Current, remixVariant);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Convert my Adventure / Sequence / Arena organizers into playlists", GUILayout.Height(22f))) ConvertOrganizersNow();
            using (new EditorGUI.DisabledScope(!S.proposals.Any(p => p.state == "new" && p.CanSave)))
                if (GUILayout.Button("Accept all that pass", GUILayout.Width(150f), GUILayout.Height(22f)))
                {
                    int n = 0; foreach (var p in S.proposals.Where(p => p.state == "new" && p.CanSave).ToList()) if (S.Accept(p)) n++;
                    status = $"Accepted {n}. Review them in Playlists, then Save."; Recheck();
                }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            foreach (string m in S.messages.Skip(Math.Max(0, S.messages.Count - 6))) GUILayout.Label(m, EditorStyles.wordWrappedMiniLabel);

            genScroll = EditorGUILayout.BeginScrollView(genScroll);
            foreach (PlaylistProposal p in S.proposals.Where(p => p.state == "new").ToList()) DrawProposal(p);
            int done = S.proposals.Count(p => p.state != "new");
            if (done > 0) GUILayout.Label($"{done} accepted or rejected.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        /// The uploader's one-click "Convert my organizers".
        public void ConvertOrganizersNow()
        {
            if (S == null) return;
            var made = S.ConvertOrganizers();
            tab = GenerateTab;
            status = $"Organizers → {made.Count} draft(s); {made.Count(p => p.CanSave)} can be accepted as-is. Your organizers are unchanged.";
            Repaint();
        }

        void Generate(PackagePlaylist remixBase, string variant)
        {
            generating = true;
            status = remixBase != null ? $"Remixing '{remixBase.name}' as {variant}…" : $"Generating {generateCount} playlists…";
            string body = PlaylistStudioApi.Body(S, remixBase != null ? 2 : generateCount, remixBase, variant, UnityEngine.Random.Range(1, 100000));
            PlaylistStudioApi.Send(contentId, body, r =>
            {
                generating = false;
                if (!r.success) { status = $"Generator: {r.error} ({r.code})"; Repaint(); return; }
                var made = S.TakeResponse(r, remixBase != null ? "remix" : "generate");
                status = $"{made.Count} proposal(s): {made.Count(p => p.CanSave)} pass every gate, {made.Count(p => p.FitsSmallRoom)} play in a 12×18 ft room. "
                    + $"({r.model}, {r.outputTokens} tokens out{(r.cacheReadTokens > 0 ? ", title context cached" : "")})";
                Repaint();
            });
        }

        void DrawProposal(PlaylistProposal p)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            var kind = S.Kinds.FirstOrDefault(k => k.id == p.playlist.kind)?.singular ?? p.playlist.kind;
            GUILayout.Label($"{(p.CanSave ? "✓" : "✕")}  {p.playlist.name}", EditorStyles.boldLabel);
            GUILayout.Label($"{kind} · {p.playlist.arc.Sum(l => l.minutes)} min · {p.source}", EditorStyles.miniLabel, GUILayout.Width(170f));
            using (new EditorGUI.DisabledScope(!p.CanSave))
                if (GUILayout.Button("Accept", EditorStyles.miniButtonLeft, GUILayout.Width(60f))) { S.Accept(p); Recheck(); }
            if (GUILayout.Button("Edit", EditorStyles.miniButtonMid, GUILayout.Width(44f))) { editingProposal = p; tab = EditTab; Recheck(); }
            if (GUILayout.Button("Reject", EditorStyles.miniButtonRight, GUILayout.Width(52f))) p.state = "rejected";
            EditorGUILayout.EndHorizontal();
            GUILayout.Label(p.playlist.pitch, EditorStyles.wordWrappedLabel);
            Rect arc = GUILayoutUtility.GetRect(0f, 58f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(arc, new Color(0.08f, 0.08f, 0.1f));
            PlaylistStudioDraw.Arc(arc, p.playlist);
            Rect chips = GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true));
            float x = chips.x;
            foreach (PlaylistFitRow row in p.fit)
            {
                string text = row.space.label + " " + (row.verdict == PlaylistFitCheck.Fits ? "✓" : row.verdict == PlaylistFitCheck.Streams ? "≈" : row.verdict == PlaylistFitCheck.Skipped ? "–" : "✕");
                float w = PlaylistStudioDraw.ChipWidth(text);
                PlaylistStudioDraw.Chip(new Rect(x, chips.y, w, 15f), text, PlaylistStudioDraw.VerdictColor(row.verdict));
                x += w + 3f;
            }
            string objectives = string.Join(" · ", p.playlist.arc.Select(l => l.objective.type + (l.objective.count > 0 ? " " + l.objective.count : "") + (l.objective.seconds > 0 ? " " + l.objective.seconds + "s" : "")));
            GUILayout.Label("Objectives: " + objectives + (p.playlist.rules.IsDefault ? "" : $"   Rules: {p.playlist.rules.advance}{(p.playlist.rules.timerSec > 0 ? " " + p.playlist.rules.timerSec + "s" : "")} {p.playlist.rules.scoring} {(p.playlist.rules.lives > 0 ? p.playlist.rules.lives + " lives" : "")}"), EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(p.designNotes)) GUILayout.Label("Why: " + p.designNotes, EditorStyles.wordWrappedMiniLabel);
            foreach (PlaylistIssue i in p.issues.Where(i => i.isError)) EditorGUILayout.HelpBox(i.message, MessageType.Error);
            if (p.fitProblem != null) EditorGUILayout.HelpBox(p.fitProblem, MessageType.Error);
            if (p.repairs.Count > 0) GUILayout.Label("Fixed for you: " + string.Join("; ", p.repairs), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        // ───────────────────────────── small widgets ─────────────────────────────

        static string Popup(string value, string[] options, float width, string emptyLabel = null)
        {
            var labels = options.Select(o => string.IsNullOrEmpty(o) ? (emptyLabel ?? "(none)") : o).ToList();
            bool hasEmpty = emptyLabel != null && !options.Contains("");
            if (hasEmpty) labels.Insert(0, emptyLabel);
            var values = hasEmpty ? new[] { "" }.Concat(options).ToArray() : options;
            int i = Array.IndexOf(values, value ?? "");
            if (i < 0) { labels.Add(value + " (?)"); values = values.Concat(new[] { value }).ToArray(); i = values.Length - 1; }
            int n = EditorGUILayout.Popup(i, labels.ToArray(), GUILayout.Width(width));
            return values[n];
        }

        static List<string> Csv(string label, List<string> values) => Csv(new GUIContent(label), values);

        static List<string> Csv(GUIContent label, List<string> values)
        {
            string joined = string.Join(", ", values ?? new List<string>());
            string edited = EditorGUILayout.DelayedTextField(label, joined);
            return edited == joined ? values : edited.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        }
    }
}
#endif
