#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace DreamPark.Diagnostics
{
    // Per-bundle size breakdown for content debugging. Reads the current
    // AddressableAssetSettings state (whatever the Smart Grouper last
    // produced) and shows, per group:
    //   - Total estimated bundle size (sum of input asset bytes)
    //   - Sorted list of entries with size + % of group
    //
    // Use case: "Bundle-A_DragonsHoard is 400 MB — what's in it?"
    // Workflow: refresh → find the bundle → expand → see top contributors
    //   → click an asset row to ping it in the Project window → decide what
    //   to optimize, downscale, or move.
    //
    // The size shown is *input asset bytes*. The actual bundle file size
    // after LZ4 compression is typically 60-80% of this for texture/mesh-
    // heavy content. Audio/video are already compressed by Unity so LZ4
    // barely helps them — their input bytes are close to their bundle
    // bytes. Treat input-bytes as a reliable upper bound on bundle size.
    public class BundleSizeBreakdown : EditorWindow
    {
        [MenuItem("DreamPark/Diagnostics/Bundle Size Breakdown...", false, 1001)]
        public static void Open()
        {
            var w = GetWindow<BundleSizeBreakdown>("Bundle Size Breakdown");
            w.minSize = new Vector2(820, 540);
            w.Show();
        }

        // Per-asset entry row in a group.
        private struct EntryInfo
        {
            public string assetPath;
            public string guid;
            public long bytes;
            public string extension;        // ".png", ".prefab", ".wav" — lowercase
        }

        // Aggregated info for one addressable group.
        private class GroupInfo
        {
            public string groupName;
            public List<EntryInfo> entries = new List<EntryInfo>();
            public long totalBytes;
        }

        private string _contentId;
        private List<GroupInfo> _groups = new List<GroupInfo>();
        private string _searchQuery = "";
        private Vector2 _scroll;
        private HashSet<string> _expandedGroups = new HashSet<string>();
        private long _grandTotalBytes;
        private DateTime _lastRefreshUtc = DateTime.MinValue;

        // Sort modes for the per-bundle entry rows.
        private enum EntrySortMode { BySizeDesc, ByPathAsc, ByExtension }
        private EntrySortMode _entrySort = EntrySortMode.BySizeDesc;

        private void OnEnable()
        {
            // Reuse whichever content title the dev last selected in the
            // Content Uploader panel.
            _contentId = EditorPrefs.GetString("DreamPark.ContentUploader.LastContentId", "");
            Refresh();
        }

        // -----------------------------------------------------------------
        // Data refresh
        // -----------------------------------------------------------------

        private void Refresh()
        {
            _groups.Clear();
            _grandTotalBytes = 0;
            _lastRefreshUtc = DateTime.UtcNow;

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;
            if (string.IsNullOrEmpty(_contentId)) return;

            string contentIdPrefix = _contentId + "-";

            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                if (!group.Name.StartsWith(contentIdPrefix, StringComparison.Ordinal)) continue;

                var info = new GroupInfo { groupName = group.Name };

                foreach (var entry in group.entries)
                {
                    string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                    if (string.IsNullOrEmpty(path)) continue;

                    long bytes = 0;
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists) bytes = fi.Length;
                    }
                    catch { /* unreadable file; size 0 */ }

                    info.entries.Add(new EntryInfo
                    {
                        assetPath = path,
                        guid = entry.guid,
                        bytes = bytes,
                        extension = Path.GetExtension(path).ToLowerInvariant(),
                    });
                    info.totalBytes += bytes;
                }

                _groups.Add(info);
                _grandTotalBytes += info.totalBytes;
            }

            // Largest groups first — Ellen's looking for the dragon's den
            // bundle and wants it at the top.
            _groups.Sort((a, b) => b.totalBytes.CompareTo(a.totalBytes));

            // Re-apply the current entry-sort mode so freshly-loaded data
            // matches whatever the user picked.
            SortEntries();
        }

        private void SortEntries()
        {
            foreach (var g in _groups)
            {
                switch (_entrySort)
                {
                    case EntrySortMode.BySizeDesc:
                        g.entries.Sort((a, b) => b.bytes.CompareTo(a.bytes));
                        break;
                    case EntrySortMode.ByPathAsc:
                        g.entries.Sort((a, b) => string.CompareOrdinal(a.assetPath, b.assetPath));
                        break;
                    case EntrySortMode.ByExtension:
                        g.entries.Sort((a, b) =>
                        {
                            int c = string.CompareOrdinal(a.extension, b.extension);
                            return c != 0 ? c : b.bytes.CompareTo(a.bytes);
                        });
                        break;
                }
            }
        }

        // -----------------------------------------------------------------
        // UI
        // -----------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Bundle Size Breakdown", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawHeader();
            EditorGUILayout.Space();
            DrawControls();
            EditorGUILayout.Space();
            DrawGroupList();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (string.IsNullOrEmpty(_contentId))
                {
                    EditorGUILayout.HelpBox(
                        "No content title selected. Open the Content Uploader panel and pick a content title once — this window reuses your last selection.",
                        MessageType.Info);
                    return;
                }

                EditorGUILayout.LabelField($"Content:     {_contentId}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Bundles:     {_groups.Count}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Total size:  {FormatBytes(_grandTotalBytes)} (input asset bytes — actual bundle ~60-80% of this after LZ4)", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Refreshed:   {_lastRefreshUtc.ToLocalTime():HH:mm:ss}", EditorStyles.miniLabel);
            }
        }

        private void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Primary action — leftmost and slightly taller so it reads
                // as the headline button. Runs the Bundle Packer: mutates
                // AddressableAssetSettings to bring groups in sync with the
                // latest content folder + the latest Smart Grouper logic.
                // Slower (a few seconds on big content) and modifies state,
                // so opt-in only — but this is what populates the breakdown
                // with current numbers after content / SDK changes.
                if (GUILayout.Button("▶  Run Bundle Packer", GUILayout.Width(180), GUILayout.Height(26)))
                    RunBundlePacker();

                GUILayout.Space(6);

                // Secondary action — fast read-only refresh. Re-reads
                // whatever's currently saved in AddressableAssetSettings
                // + on-disk file sizes without re-organizing groups. Use
                // when groups are already up-to-date and you just want a
                // fresh size snapshot.
                if (GUILayout.Button("Refresh", GUILayout.Width(80), GUILayout.Height(26)))
                    Refresh();

                if (GUILayout.Button("Expand All", GUILayout.Width(90), GUILayout.Height(26)))
                {
                    foreach (var g in _groups) _expandedGroups.Add(g.groupName);
                }
                if (GUILayout.Button("Collapse All", GUILayout.Width(100), GUILayout.Height(26)))
                {
                    _expandedGroups.Clear();
                }

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("Sort entries by:", GUILayout.Width(100));
                var newSort = (EntrySortMode)EditorGUILayout.EnumPopup(_entrySort, GUILayout.Width(140));
                if (newSort != _entrySort)
                {
                    _entrySort = newSort;
                    SortEntries();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                _searchQuery = EditorGUILayout.TextField(_searchQuery);
                if (!string.IsNullOrEmpty(_searchQuery) && GUILayout.Button("✕", GUILayout.Width(24)))
                    _searchQuery = "";
            }
        }

        private void RunBundlePacker()
        {
            if (string.IsNullOrEmpty(_contentId))
            {
                EditorUtility.DisplayDialog(
                    "No content title",
                    "Open the Content Uploader panel and select a content title once before running the Bundle Packer.",
                    "OK");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar(
                    "Bundle Size Breakdown",
                    $"Running Bundle Packer for {_contentId}...",
                    0.5f);

                // Same call the upload pipeline makes. Walks the content
                // folder, applies addressable labeling, then (if strategy
                // is Smart) calls SmartBundleGrouper.ApplyDependencyAwareGrouping.
                ContentProcessor.ForceUpdateContent(_contentId);

                EditorUtility.ClearProgressBar();
                Refresh();
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[BundleSizeBreakdown] Bundle Packer failed: {e}");
                EditorUtility.DisplayDialog(
                    "Bundle Packer failed",
                    $"Couldn't run Bundle Packer:\n\n{e.Message}\n\nSee Console for details.",
                    "OK");
            }
        }

        private void DrawGroupList()
        {
            if (_groups.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No groups found for this content title. Click 'Run Bundle Packer' above to populate the addressable groups now (no build / upload required — just organizes assets into bundles). If you've never run an upload, this will also be the first time the groups get created.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            string q = _searchQuery?.Trim() ?? "";
            bool hasSearch = q.Length > 0;

            foreach (var group in _groups)
            {
                // Search filter: show the group if its name matches OR any of
                // its entries match. When matching entries, only those rows
                // render inside the foldout (filtering down the contents to
                // what the dev's actually looking for).
                bool groupNameMatches = !hasSearch
                    || group.groupName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

                List<EntryInfo> visibleEntries = group.entries;
                if (hasSearch && !groupNameMatches)
                {
                    visibleEntries = group.entries
                        .Where(e => e.assetPath.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    if (visibleEntries.Count == 0) continue;
                }

                DrawGroupRow(group, visibleEntries, hasSearch);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawGroupRow(GroupInfo group, List<EntryInfo> visibleEntries, bool forceExpand)
        {
            bool wasExpanded = _expandedGroups.Contains(group.groupName);
            bool isExpanded = wasExpanded || forceExpand;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool nowExpanded = EditorGUILayout.Foldout(isExpanded, group.groupName, true, EditorStyles.foldout);
                    if (nowExpanded != wasExpanded && !forceExpand)
                    {
                        if (nowExpanded) _expandedGroups.Add(group.groupName);
                        else _expandedGroups.Remove(group.groupName);
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"{FormatBytes(group.totalBytes)} · {group.entries.Count} entries",
                        EditorStyles.miniLabel, GUILayout.Width(220));
                }

                if (!isExpanded) return;

                if (visibleEntries.Count == 0)
                {
                    EditorGUILayout.LabelField("  (empty)", EditorStyles.miniLabel);
                    return;
                }

                foreach (var entry in visibleEntries)
                {
                    DrawEntryRow(entry, group.totalBytes);
                }
            }
        }

        private void DrawEntryRow(EntryInfo entry, long groupTotal)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Indent slightly for readability inside the foldout.
                GUILayout.Space(14);

                // Clickable path: clicking pings the asset in the Project
                // window so the dev can jump to it. Using a button style
                // that renders flat / link-like.
                if (GUILayout.Button(entry.assetPath, EditorStyles.label, GUILayout.ExpandWidth(true)))
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(entry.assetPath);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }

                double pct = groupTotal > 0 ? 100.0 * entry.bytes / groupTotal : 0;
                EditorGUILayout.LabelField(FormatBytes(entry.bytes), GUILayout.Width(80));
                EditorGUILayout.LabelField($"{pct:F1}%", EditorStyles.miniLabel, GUILayout.Width(50));
            }
        }

        // -----------------------------------------------------------------
        // Utility
        // -----------------------------------------------------------------

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024d && u < units.Length - 1) { v /= 1024d; u++; }
            return $"{v:0.##} {units[u]}";
        }
    }
}
#endif
