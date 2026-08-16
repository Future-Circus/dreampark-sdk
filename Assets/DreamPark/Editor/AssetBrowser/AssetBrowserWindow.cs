#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Browse the DreamPark asset library and pull models straight into a
    /// content folder, imported and ready to use.
    ///
    /// Layout follows the optimizer windows (Texture / Audio / Animation /
    /// Material): header, controls, summary bar, body. Those four are
    /// deliberately the same shape as each other so creators do not have to
    /// learn a new panel per tool, and this is the fifth.
    ///
    /// ── THREE IMGUI RULES THIS WINDOW CANNOT BREAK ─────────────────────────
    ///
    /// 1. The row list is swapped in ONLY on EventType.Layout. IMGUI assigns
    ///    control ids by draw order, so a thumbnail arriving mid-frame and
    ///    adding a card between the Layout and Repaint passes shifts every id
    ///    after it and clicks land on the wrong card. ContentUploaderPanel
    ///    documents the same hazard for the same reason.
    /// 2. Card sub-rects are computed unconditionally. A GUI.Button drawn only
    ///    sometimes corrupts the id stream just as badly.
    /// 3. Every cached Texture2D is destroyed in OnDisable. Script-created
    ///    textures are not owned by the asset database and only an editor
    ///    restart reclaims them otherwise.
    ///
    /// Async callbacks never draw. They mutate a field and call Repaint().
    /// </summary>
    public class AssetBrowserWindow : EditorWindow
    {
        [MenuItem("DreamPark/Asset Browser", false, 4)]
        public static void Open()
        {
            var w = GetWindow<AssetBrowserWindow>("Asset Browser");
            w.minSize = new Vector2(720, 480);
            w.Show();
        }

        private const string PrefContentId = "DreamPark.AssetBrowser.ContentId";
        private const string PrefType = "DreamPark.AssetBrowser.Type";

        private const float CardWidth = 132f;
        private const float CardSpacing = 8f;
        private const float CardImageHeight = 116f;
        private const float CardLabelHeight = 30f;
        // double, because it is compared against EditorApplication.timeSinceStartup.
        private const double SearchDebounceSeconds = 0.4;

        // The facets worth offering in a tool whose job is importing meshes.
        private static readonly string[] TypeKeys = { "model", "rig", "all" };
        private static readonly string[] TypeLabels = { "3D Models", "Rigs & Animation", "Everything" };

        private readonly DreamieQuery _query = new DreamieQuery();

        /* ── live state, written by async callbacks ───────────────────────
         *
         * Nothing below is read while drawing. Every field that gates a
         * GUILayout control is copied into its `_draw*` twin on Layout, so a
         * response landing between the Layout and Repaint passes can never
         * add or remove a control mid-frame. Rule 1 applies to all of it, not
         * just the rows — `_progress` flipping from -1 adds a whole
         * ProgressBar, which is exactly the kind of change that shifts every
         * control id after it.
         */
        private List<DreamieAsset> _rows = new List<DreamieAsset>();
        private string _nextCursor;
        private bool _degraded;

        /* ── draw state, swapped in on Layout only ────────────────────── */
        private List<DreamieAsset> _drawRows = new List<DreamieAsset>();
        private bool _drawHasMore;
        private bool _drawDegraded;
        private string _drawError;
        private float _drawProgress = -1f;
        private string _drawStatus;
        private bool _drawLoading;

        private List<DreamieJob> _jobs = new List<DreamieJob>();
        private List<DreamieCreator> _creators = new List<DreamieCreator>();

        private ThumbnailCache _thumbs;
        private Vector2 _scroll;

        private bool _loading;
        private string _error;
        private string _status;
        private float _progress = -1f;

        private string _contentId;
        private string[] _contentFolders = new string[0];

        private string _searchField = "";
        private double _searchDueAt;

        /* ── lifecycle ─────────────────────────────────────────────────── */

        private void OnEnable()
        {
            _thumbs = new ThumbnailCache();
            _thumbs.Changed += Repaint;

            _query.type = EditorPrefs.GetString(PrefType, "model");
            // Keep the format filter consistent with a restored type — see the
            // type dropdown handler.
            _query.format = _query.type == "all" ? null : "fbx";
            _contentId = EditorPrefs.GetString(PrefContentId, "");

            AuthAPI.LoginStateChanged += OnLoginStateChanged;
            EditorApplication.update += OnEditorUpdate;

            RefreshContentFolders();
            if (AuthAPI.isLoggedIn) ReloadAll();
        }

        private void OnDisable()
        {
            AuthAPI.LoginStateChanged -= OnLoginStateChanged;
            EditorApplication.update -= OnEditorUpdate;

            // Rule 3. Without this every thumbnail ever loaded stays resident
            // until the editor restarts.
            _thumbs?.Dispose();
            _thumbs = null;
        }

        private void OnFocus()
        {
            // A creator can rename YOUR_GAME_HERE or add a content folder while
            // this window is open; the dropdown must not go stale.
            RefreshContentFolders();
        }

        private void OnLoginStateChanged(bool loggedIn)
        {
            if (loggedIn) ReloadAll();
            else { _rows.Clear(); _jobs.Clear(); _creators.Clear(); }
            Repaint();
        }

        private void OnEditorUpdate()
        {
            // Debounced search. Refetching on every keystroke would fire a
            // request per character typed.
            if (_searchDueAt > 0 && EditorApplication.timeSinceStartup >= _searchDueAt)
            {
                _searchDueAt = 0;
                _query.search = _searchField;
                ReloadRows();
            }
        }

        /* ── data ──────────────────────────────────────────────────────── */

        private void ReloadAll()
        {
            ReloadRows();

            DreamieCatalogApi.Jobs((jobs, err) =>
            {
                if (jobs != null) _jobs = jobs;
                Repaint();
            });

            DreamieCatalogApi.Facets(null, (creators, counts, err) =>
            {
                if (creators != null) _creators = creators;
                Repaint();
            });
        }

        /// <summary>
        /// Bumped on every filter change. A response carrying a stale
        /// generation is dropped.
        ///
        /// Without this, changing a filter while a page is in flight showed
        /// rows that contradicted the controls: the new query was refused by
        /// the `_loading` guard, and then the OLD query's response arrived and
        /// installed itself. No error, no retry, and the only tell was that
        /// the grid disagreed with the dropdowns.
        /// </summary>
        private int _generation;

        private void ReloadRows()
        {
            _generation++;
            _query.cursor = null;
            _rows = new List<DreamieAsset>();
            _nextCursor = null;
            FetchPage(append: false);
        }

        private void FetchPage(bool append)
        {
            // Only paging is refused while busy — a filter change must always
            // win, because it is the more recent statement of intent.
            if (_loading && append) return;

            int generation = _generation;
            _loading = true;
            _error = null;

            _query.cursor = append ? _nextCursor : null;

            DreamieCatalogApi.List(_query, (page, err) =>
            {
                if (generation != _generation) return;   // superseded
                _loading = false;

                if (err != null)
                {
                    _error = err;
                    Repaint();
                    return;
                }

                var next = append ? new List<DreamieAsset>(_rows) : new List<DreamieAsset>();

                // Mark what this content folder already has, so a second
                // download of the same shrimp is a visible choice rather than
                // an accident.
                var imported = string.IsNullOrEmpty(_contentId)
                    ? new Dictionary<string, string>()
                    : DreamieFolders.ImportedAssetIds(_contentId);

                foreach (var a in page.assets)
                {
                    if (imported.TryGetValue(a.id, out var path))
                    {
                        a.alreadyImported = true;
                        a.importedAtPath = path;
                    }
                    next.Add(a);
                }

                _nextCursor = page.nextCursor;
                _degraded = page.degraded;
                _rows = next;
                Repaint();
            });
        }

        private void RefreshContentFolders()
        {
            _contentFolders = ContentFolders.UserContentFolderNames()?.ToArray() ?? new string[0];
            if (_contentFolders.Length == 0) { _contentId = ""; return; }
            if (string.IsNullOrEmpty(_contentId) || Array.IndexOf(_contentFolders, _contentId) < 0)
                _contentId = ContentFolders.GameFolderName();
            if (Array.IndexOf(_contentFolders, _contentId) < 0) _contentId = _contentFolders[0];
            // Persist the defaulted choice too, so it is sticky without the
            // creator having to touch the popup first.
            EditorPrefs.SetString(PrefContentId, _contentId);
        }

        /* ── draw ──────────────────────────────────────────────────────── */

        private void OnGUI()
        {
            // Rule 1, for everything that gates a control.
            if (Event.current.type == EventType.Layout)
            {
                _drawRows = _rows;
                _drawHasMore = !string.IsNullOrEmpty(_nextCursor);
                _drawDegraded = _degraded;
                _drawError = _error;
                _drawProgress = _progress;
                _drawStatus = _status;
                _drawLoading = _loading;
            }

            if (!AuthAPI.isLoggedIn)
            {
                // Reused rather than reimplemented — SDKPublishPanel calls the
                // same one.
                ContentUploaderPanel.DrawLoginGate("Sign in to browse the DreamPark asset library.");
                return;
            }

            DrawHeader();
            EditorGUILayout.Space(4);
            DrawControls();
            EditorGUILayout.Space(4);
            DrawSummaryBar();
            EditorGUILayout.Space(4);
            DrawGrid();
            DrawFooter();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("DreamPark Asset Browser", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Everything Dreamie has generated. Pick models and pull them in with DreamPark import "
                    + "settings, materials and shader already applied.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (!string.IsNullOrEmpty(_drawError))
                EditorGUILayout.HelpBox(_drawError, MessageType.Error);

            if (_drawDegraded)
                EditorGUILayout.HelpBox(
                    "The asset index is still building, so this is one unsorted page and there is no "
                    + "\"Load more\". Filters will settle in a few minutes.", MessageType.Warning);
        }

        private void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                string typed = EditorGUILayout.TextField(_searchField);
                if (typed != _searchField)
                {
                    _searchField = typed;
                    _searchDueAt = EditorApplication.timeSinceStartup + SearchDebounceSeconds;
                }
                if (GUILayout.Button("✕", GUILayout.Width(24)) && _searchField.Length > 0)
                {
                    _searchField = "";
                    _query.search = "";
                    _searchDueAt = 0;   // cancel the pending debounce, or it fires again in 0.4s
                    GUI.FocusControl(null);
                    ReloadRows();
                }

                int typeIndex = Mathf.Max(0, Array.IndexOf(TypeKeys, _query.type));
                int newType = EditorGUILayout.Popup(typeIndex, TypeLabels, GUILayout.Width(140));
                if (newType != typeIndex)
                {
                    _query.type = TypeKeys[newType];
                    // "Everything" has to mean it. Leaving the FBX filter on
                    // would hide every render, sound and script under a label
                    // that promises the opposite — and would make the "no FBX"
                    // badge on a card unreachable, since nothing without one
                    // could ever be listed.
                    _query.format = _query.type == "all" ? null : "fbx";
                    EditorPrefs.SetString(PrefType, _query.type);
                    ReloadRows();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                /* Job (batch) */
                var jobLabels = new List<string> { "All batches" };
                jobLabels.AddRange(_jobs.Select(j => j.Label.Replace("/", "∕")));
                int jobIndex = string.IsNullOrEmpty(_query.jobId)
                    ? 0
                    : Mathf.Max(0, _jobs.FindIndex(j => j.id == _query.jobId) + 1);
                int newJob = EditorGUILayout.Popup(jobIndex, jobLabels.ToArray(), GUILayout.Width(220));
                if (newJob != jobIndex)
                {
                    _query.jobId = newJob == 0 ? null : _jobs[newJob - 1].id;
                    ReloadRows();
                }

                /* Creator */
                var creatorLabels = new List<string> { "Anyone" };
                creatorLabels.AddRange(_creators.Select(c => $"{c.creatorName} ({c.count})".Replace("/", "∕")));
                int creatorIndex = string.IsNullOrEmpty(_query.creatorId)
                    ? 0
                    : Mathf.Max(0, _creators.FindIndex(c => c.creatorId == _query.creatorId) + 1);
                int newCreator = EditorGUILayout.Popup(creatorIndex, creatorLabels.ToArray(), GUILayout.Width(180));
                if (newCreator != creatorIndex)
                {
                    _query.creatorId = newCreator == 0 ? null : _creators[newCreator - 1].creatorId;
                    ReloadRows();
                }

                bool favs = GUILayout.Toggle(_query.favoritesOnly, "★ Favorites", EditorStyles.miniButton, GUILayout.Width(90));
                if (favs != _query.favoritesOnly)
                {
                    _query.favoritesOnly = favs;
                    ReloadRows();
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(VisibleRows().Count == 0))
                {
                    if (GUILayout.Button("✓ Select All", GUILayout.Width(96))) SetSelectedAll(true);
                    if (GUILayout.Button("✗ Deselect All", GUILayout.Width(104))) SetSelectedAll(false);
                }
            }
        }

        private void DrawSummaryBar()
        {
            var selected = _drawRows.Where(r => r.selected).ToList();
            long bytes = selected.Sum(r => r.bytes);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string size = bytes > 0 ? $"  ·  ~{EditorUtility.FormatBytes(bytes)}" : "";
                EditorGUILayout.LabelField(
                    $"Showing: {_drawRows.Count}   |   Selected: {selected.Count}{size}"
                    + (_drawLoading ? "   |   loading…" : ""),
                    EditorStyles.miniLabel);

                if (_drawProgress >= 0f)
                {
                    Rect r = GUILayoutUtility.GetRect(18f, 18f, "TextField");
                    EditorGUI.ProgressBar(r, Mathf.Clamp01(_drawProgress), _drawStatus ?? "");
                }
            }
        }

        private void DrawGrid()
        {
            var visible = VisibleRows();

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                if (visible.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        _drawLoading
                            ? "Loading…"
                            : "Nothing here. Try a different search, batch or creator — or switch the type "
                              + "filter to Everything.",
                        MessageType.Info);
                    return;
                }

                float panelWidth = Mathf.Max(position.width - 28f, CardWidth);
                int perRow = Mathf.Max(1, Mathf.FloorToInt((panelWidth + CardSpacing) / (CardWidth + CardSpacing)));

                for (int i = 0; i < visible.Count; i += perRow)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        for (int c = 0; c < perRow && i + c < visible.Count; c++)
                            DrawCard(visible[i + c]);
                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.Space(CardSpacing);
                }

                if (_drawHasMore)
                {
                    EditorGUILayout.Space(6);
                    using (new EditorGUI.DisabledScope(_drawLoading))
                        if (GUILayout.Button("Load more", GUILayout.Height(26)))
                            FetchPage(append: true);
                }
            }
        }

        private void DrawCard(DreamieAsset a)
        {
            // Rule 2: every rect below is computed unconditionally, whatever
            // state the card is in.
            Rect card = GUILayoutUtility.GetRect(
                CardWidth, CardWidth, CardImageHeight + CardLabelHeight, CardImageHeight + CardLabelHeight,
                GUILayout.Width(CardWidth), GUILayout.Height(CardImageHeight + CardLabelHeight));

            var imageRect = new Rect(card.x, card.y, card.width, CardImageHeight);
            var labelRect = new Rect(card.x, card.y + CardImageHeight, card.width, CardLabelHeight);
            var checkRect = new Rect(card.x + 3, card.y + 3, 18, 18);
            var starRect = new Rect(card.xMax - 21, card.y + 3, 18, 18);
            var badgeRect = new Rect(card.x + 3, card.yMax - CardLabelHeight - 19, card.width - 6, 16);

            // The frame is drawn whatever happens, so a still-loading cell
            // holds its place instead of collapsing and reflowing the grid
            // under the pointer.
            EditorGUI.DrawRect(imageRect, a.selected
                ? new Color(0.30f, 0.60f, 0.35f, 0.35f)
                : new Color(0f, 0f, 0f, 0.18f));

            var tex = _thumbs?.Get(a.previewUrl);
            if (tex != null)
            {
                GUI.DrawTexture(imageRect, tex, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(imageRect,
                    _thumbs != null && _thumbs.Failed(a.previewUrl) ? "no preview" : "…",
                    EditorStyles.centeredGreyMiniLabel);
            }

            if (a.alreadyImported)
            {
                EditorGUI.DrawRect(badgeRect, new Color(0.20f, 0.45f, 0.75f, 0.75f));
                GUI.Label(badgeRect, "  in project", EditorStyles.miniLabel);
            }
            else if (!a.HasFbx)
            {
                EditorGUI.DrawRect(badgeRect, new Color(0.55f, 0.35f, 0.10f, 0.75f));
                GUI.Label(badgeRect, "  no FBX", EditorStyles.miniLabel);
            }

            GUI.Label(labelRect, new GUIContent(a.name, Tooltip(a)), EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(!a.HasFbx))
            {
                bool now = GUI.Toggle(checkRect, a.selected, GUIContent.none);
                if (now != a.selected) a.selected = now;
            }

            bool fav = GUI.Toggle(starRect, a.isFavorite, a.isFavorite ? "★" : "☆", EditorStyles.label);
            if (fav != a.isFavorite)
            {
                a.isFavorite = fav;
                DreamieCatalogApi.SetFavorite(a.id, fav, _ => Repaint());
            }

            // Click the image to toggle — but only the image, and decided by
            // RECT rather than by event consumption. GUI.Toggle consumes on
            // MouseDown and reports on MouseUp, so testing "did a control eat
            // this" would toggle twice on the checkbox itself.
            if (Event.current.type == EventType.MouseDown
                && imageRect.Contains(Event.current.mousePosition)
                && !checkRect.Contains(Event.current.mousePosition)
                && !starRect.Contains(Event.current.mousePosition))
            {
                if (a.HasFbx)
                {
                    a.selected = !a.selected;
                    Event.current.Use();
                    Repaint();
                }
            }
        }

        private static string Tooltip(DreamieAsset a)
        {
            var parts = new List<string> { a.name };
            if (!string.IsNullOrEmpty(a.creatorName)) parts.Add("by " + a.creatorName);
            if (a.bytes > 0) parts.Add(EditorUtility.FormatBytes(a.bytes));
            if (a.tags != null && a.tags.Count > 0) parts.Add(string.Join(", ", a.tags.Take(6)));
            if (!string.IsNullOrEmpty(a.importedAtPath)) parts.Add("already at " + a.importedAtPath);
            if (!a.HasFbx) parts.Add("This asset ships no FBX, so Unity cannot import it.");
            return string.Join("\n", parts);
        }

        private void DrawFooter()
        {
            var selected = _drawRows.Where(r => r.selected && r.HasFbx).ToList();

            if (_contentFolders.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No content folder yet. Create one under Assets/Content (the setup popup renames "
                    + "YOUR_GAME_HERE) before importing.", MessageType.Warning);
                return;
            }

            bool reserved = ContentFolders.IsReserved(_contentId);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("Import into:", GUILayout.Width(72));
                int idx = Mathf.Max(0, Array.IndexOf(_contentFolders, _contentId));
                int next = EditorGUILayout.Popup(idx, _contentFolders, GUILayout.Width(180));
                if (next != idx)
                {
                    _contentId = _contentFolders[next];
                    EditorPrefs.SetString(PrefContentId, _contentId);
                    // "Already in project" is per content folder, so switching
                    // changes which cards carry the badge.
                    ReloadRows();
                }

                using (new EditorGUI.DisabledScope(selected.Count == 0 || _drawLoading || _drawProgress >= 0f || reserved))
                {
                    if (GUILayout.Button(
                            selected.Count == 0 ? "Download" : $"Download {selected.Count}",
                            GUILayout.Width(130), GUILayout.Height(24)))
                        StartDownload(selected);
                }
            }

            if (reserved)
                EditorGUILayout.HelpBox(
                    $"“{_contentId}” is a reserved folder, so nothing can be imported into it. "
                    + "Name your game first (DreamPark → Content Uploader).", MessageType.Warning);
        }

        /* ── actions ───────────────────────────────────────────────────── */

        /// <summary>What is on screen right now — the Layout snapshot, never the live list.</summary>
        private List<DreamieAsset> VisibleRows() => _drawRows;

        /// <summary>
        /// Applies to what is ON SCREEN, deliberately. Search for "shrimp",
        /// hit Select All, and you have selected the shrimp — not everything
        /// that has ever been loaded into this window.
        /// </summary>
        private void SetSelectedAll(bool selected)
        {
            foreach (var r in VisibleRows())
                if (r.HasFbx) r.selected = selected;
            Repaint();
        }

        private void StartDownload(List<DreamieAsset> selected)
        {
            var already = selected.Where(s => s.alreadyImported).ToList();
            if (already.Count > 0)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Already in this project",
                    $"{already.Count} of these are already in {_contentId}:\n\n"
                    + string.Join("\n", already.Take(6).Select(a => "• " + a.name))
                    + (already.Count > 6 ? $"\n…and {already.Count - 6} more" : "")
                    + "\n\nRe-downloading replaces the files in place, keeping their GUIDs — so prefabs "
                    + "and scenes using them keep working, but any import settings you changed by hand "
                    + "are overwritten.",
                    "Re-download", "Cancel");
                if (!proceed) return;
            }

            var jobsById = _jobs.Where(j => !string.IsNullOrEmpty(j.id))
                                .GroupBy(j => j.id)
                                .ToDictionary(g => g.Key, g => g.First());

            _progress = 0f;
            _status = "Starting…";

            DreamieDownloader.Run(
                selected, _contentId, jobsById,
                (p, msg) => { _progress = p; _status = msg; Repaint(); },
                summary =>
                {
                    _progress = -1f;
                    _status = null;

                    foreach (var item in summary.items)
                    {
                        if (!string.IsNullOrEmpty(item.error))
                            Debug.LogWarning($"[Dreamie] {item.asset.name}: {item.error}");
                        foreach (var w in item.warnings)
                            Debug.LogWarning($"[Dreamie] {item.asset.name}: {w}");
                    }

                    var first = summary.items.FirstOrDefault(i => i.imported);
                    if (first != null)
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(first.targetFbxPath);
                        if (obj != null) EditorGUIUtility.PingObject(obj);
                    }

                    string detail = summary.ToString();
                    if (summary.items.Any(i => i.warnings.Count > 0))
                        detail += "\n\nSome assets came in with warnings — see the Console.";

                    EditorUtility.DisplayDialog("DreamPark Asset Browser", detail, "OK");

                    foreach (var a in _rows) a.selected = false;
                    ReloadRows();
                });
        }
    }
}
#endif
