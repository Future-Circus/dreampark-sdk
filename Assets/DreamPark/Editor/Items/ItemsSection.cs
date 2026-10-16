#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Items
{
    // The Content Uploader's "Items" group (Park Assets, under Badges). Same
    // chrome and the same IMGUI discipline as the Badges group — a foldout over
    // a wrapping card grid, list mutations applied only at the start of a Layout
    // pass, the draft flushed to .items.json between frames — but kept in its
    // own class so the 10k-line panel only hosts it (Refresh / OnLayoutPass /
    // Draw / PushSilently).
    //
    // Card: icon · Title · ID (locked when it came from Lua) · Type · Rarity ·
    // Stacks · Description, plus a status mark: ✓ when Lua awards it, a
    // warning when it's hand-added and unawarded, or a unique item whose
    // Stacks is off (the server would drop its per-instance metadata).
    public sealed class ItemsSection
    {
        private const string FoldPrefKey = "DreamPark.ContentUploader.Fold.ParkAssets.Items";
        private const float CardWidth = 340f;
        private const float CardHeight = 118f;
        private const float IconSize = 64f;
        private const float Spacing = 8f;

        private List<ItemStore.Entry> items = new List<ItemStore.Entry>();
        private string itemsContentId;
        private ItemLuaScanner.Result scan;
        private bool dirty;
        private bool addRequested;
        private int removeIndex = -1;
        private bool pushing;
        // Read lazily: the panel constructs this from a field initializer, and
        // EditorPrefs may not be called from a ScriptableObject constructor.
        private bool? fold;

        public int Count { get { return items.Count; } }
        public IReadOnlyList<ItemStore.Entry> Items { get { return items; } }

        /// Re-scan Lua and reload the draft for this content (flushes the old one first).
        public void Refresh(string contentId)
        {
            Flush();
            itemsContentId = contentId;
            addRequested = false;
            removeIndex = -1;
            dirty = false;
            scan = null;

            if (string.IsNullOrEmpty(contentId)) { items = new List<ItemStore.Entry>(); return; }
            try
            {
                scan = ItemLuaScanner.Scan(contentId);
                items = ItemStore.Merge(ItemStore.Load(contentId), scan.discoveries);
                // Persist what the scan found (new locked cards, @item titles)
                // so the draft on disk — and in git — matches the panel. Save
                // is a no-op when nothing changed and never creates an empty file.
                dirty = true;
                Flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Items] Lua scan failed: " + e.Message);
                items = ItemStore.Load(contentId);
            }
        }

        /// Call at the START of every Layout pass, before anything draws.
        public void OnLayoutPass(Action repaint)
        {
            if (addRequested)
            {
                addRequested = false;
                items.Add(new ItemStore.Entry());
                dirty = true;
                repaint?.Invoke();
            }
            if (removeIndex >= 0)
            {
                if (removeIndex < items.Count) items.RemoveAt(removeIndex);
                removeIndex = -1;
                dirty = true;
                repaint?.Invoke();
            }
            Flush();
        }

        private void Flush()
        {
            if (!dirty) return;
            dirty = false;
            if (string.IsNullOrEmpty(itemsContentId)) return;
            ItemStore.Save(itemsContentId, items);
        }

        public void Draw(string contentId, float panelWidth, bool uploadsBlocked, Action repaint)
        {
            GUILayout.Space(4);
            if (!fold.HasValue) fold = EditorPrefs.GetBool(FoldPrefKey, true);
            bool newFold = EditorGUILayout.Foldout(fold.Value, $"Items ({items.Count})", true);
            if (newFold != fold.Value)
            {
                fold = newFold;
                EditorPrefs.SetBool(FoldPrefKey, newFold);
            }
            if (!fold.Value) return;

            EditorGUI.indentLevel++;

            if (items.Count == 0)
            {
                Row(() => EditorGUILayout.HelpBox(
                    "No items yet. Call dp.profile.awardItem(\"some_id\") (or awardUniqueItem) from your Lua and it " +
                    "appears here with the ID filled in. For ids built at runtime, add a comment line like " +
                    "-- @item cr_fox \"Fox\". Or press Add Item to define one by hand.",
                    MessageType.Info));
            }
            else
            {
                float width = Mathf.Max(panelWidth - 24f, CardWidth);
                int perRow = Mathf.Max(1, Mathf.FloorToInt((width + Spacing) / (CardWidth + Spacing)));
                for (int i = 0; i < items.Count; i += perRow)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUI.indentLevel * 12f);
                    for (int j = 0; j < perRow && i + j < items.Count; j++)
                    {
                        DrawCard(items[i + j], i + j);
                        if (j < perRow - 1) GUILayout.Space(Spacing);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(Spacing);
                }
            }

            if (scan != null && scan.unresolved.Count > 0)
            {
                var names = scan.unresolved.Take(4)
                    .Select(u => $"{Path.GetFileName(u.scriptPath)}: {u.expression}").ToArray();
                Row(() => EditorGUILayout.HelpBox(
                    "Some item awards name an ID this scan can't read (built at runtime, or passed through a helper). "
                        + "Declare each id in that script with a comment line  -- @item <id> \"Name\"  (or add it by hand):\n  "
                        + string.Join("\n  ", names)
                        + (scan.unresolved.Count > names.Length ? $"\n  ...and {scan.unresolved.Count - names.Length} more" : ""),
                    MessageType.Info));
            }

            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 12f);
            if (GUILayout.Button(new GUIContent("Add Item", "Define an item that isn't referenced from Lua yet."),
                    GUILayout.Width(100), GUILayout.Height(22)))
                addRequested = true;
            if (GUILayout.Button(new GUIContent("Rescan Lua",
                    "Re-read this content folder's .lua/.lua.txt files (calls, @var, -- @item lines) and prefabs for item IDs."),
                    GUILayout.Width(100), GUILayout.Height(22)))
                Refresh(contentId);
            GUILayout.FlexibleSpace();

            // Like badges, available WITHOUT a build: item text and icons are
            // catalog metadata on the backend, not bundle content.
            using (new EditorGUI.DisabledScope(pushing || uploadsBlocked || string.IsNullOrEmpty(contentId) || items.Count == 0))
            {
                if (GUILayout.Button(new GUIContent(pushing ? "Pushing..." : "Push Items to Portal",
                        "Saves every item above to the items catalog (POST /admin/content/:id/items/save). No build required."),
                        GUILayout.Width(160), GUILayout.Height(22)))
                {
                    dirty = true;
                    Flush();
                    pushing = true;
                    ItemUploader.UploadAll(contentId, new List<ItemStore.Entry>(items), true, report =>
                    {
                        pushing = false;
                        repaint?.Invoke();
                    });
                }
            }
            GUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private static void Row(Action draw)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 12f);
            draw();
            GUILayout.EndHorizontal();
        }

        /// Status for one card: (isWarning, tooltip). Same rules as ItemAwardCheck.
        public static KeyValuePair<bool, string> StatusOf(ItemStore.Entry e)
        {
            if (e.awardedUnique && !e.stackable)
                return new KeyValuePair<bool, string>(true,
                    "Your Lua awards this as a UNIQUE item (with per-instance metadata), but Stacks is off. "
                    + "A non-stacking catalog item is a singleton and the server drops award metadata. Tick Stacks.");
            if (!e.IdLocked)
                return new KeyValuePair<bool, string>(true,
                    "Added by hand: no script in this package awards this id yet.");
            return new KeyValuePair<bool, string>(false, e.discoveredIn);
        }

        private void DrawCard(ItemStore.Entry entry, int index)
        {
            Rect card = GUILayoutUtility.GetRect(CardWidth, CardHeight, GUILayout.Width(CardWidth), GUILayout.Height(CardHeight));
            EditorGUI.DrawRect(card, new Color(0f, 0f, 0f, 0.18f));

            int prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 40f;

            var iconRect = new Rect(card.x + 6f, card.y + 6f, IconSize, IconSize);
            float fx = card.x + 6f + IconSize + 8f;
            float fw = card.xMax - fx - 24f;

            EditorGUI.BeginChangeCheck();

            var icon = (Texture2D)EditorGUI.ObjectField(iconRect,
                string.IsNullOrEmpty(entry.iconAssetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(entry.iconAssetPath),
                typeof(Texture2D), false);

            string newTitle = EditorGUI.TextField(new Rect(fx, card.y + 6f, fw, 18f), "Title", entry.name ?? "");

            string newId;
            using (new EditorGUI.DisabledScope(entry.IdLocked))
            {
                newId = EditorGUI.TextField(new Rect(fx, card.y + 28f, fw, 18f),
                    new GUIContent("ID", entry.IdLocked
                        ? entry.discoveredIn + "\n\nThis ID comes from your Lua, so it can't be edited here."
                        : "The ID your Lua passes to dp.profile.awardItem(). Letters, numbers, _ and - only. "
                          + "Item ids are global: prefix them (cr_fox) so no other title owns them."),
                    entry.itemId ?? "");
            }

            string newType = EditorGUI.TextField(new Rect(fx, card.y + 50f, fw * 0.5f - 2f, 18f),
                new GUIContent("Type", "Free text the game can filter on (getItems{type=…}), e.g. creature, potion, wand."),
                entry.type ?? "");

            // A custom rarity (not in the list) shows as "— rarity —" and is
            // kept unless the popup itself is changed.
            int rarityIndex = Mathf.Max(0, Array.IndexOf(ItemStore.Rarities, entry.rarity ?? ""));
            var rarityLabels = ItemStore.Rarities.Select(r => string.IsNullOrEmpty(r) ? "— rarity —" : r).ToArray();
            int newRarity = EditorGUI.Popup(new Rect(fx + fw * 0.5f + 2f, card.y + 50f, fw * 0.5f - 2f, 18f), rarityIndex, rarityLabels);

            bool newStack = EditorGUI.ToggleLeft(new Rect(card.x + 6f, card.y + 74f, IconSize + 6f, 18f),
                new GUIContent("Stacks", "On (default): repeat awards add up, and awardUniqueItem/metadata awards mint "
                    + "one instance each (needed for unique items).\nOff: a one-of-a-kind item the player either has or "
                    + "doesn't; re-awarding is a no-op and award metadata is ignored."),
                entry.stackable);

            string newDesc = EditorGUI.TextField(new Rect(fx, card.y + 72f, fw, 18f), "Desc", entry.description ?? "");

            if (EditorGUI.EndChangeCheck())
            {
                entry.name = newTitle;
                entry.description = newDesc;
                entry.type = newType;
                if (newRarity != rarityIndex) entry.rarity = ItemStore.Rarities[newRarity];
                entry.stackable = newStack;
                if (!entry.IdLocked) entry.itemId = ItemStore.SanitizeId(newId);
                entry.iconAssetPath = icon != null ? AssetDatabase.GetAssetPath(icon) : "";
                dirty = true;
            }

            string sourceLabel = entry.IdLocked
                ? (entry.awardedUnique ? "● From your Lua · unique" : "● From your Lua")
                : "○ Added by hand";
            GUI.Label(new Rect(fx, card.y + 96f, fw - 40f, 14f),
                new GUIContent(sourceLabel, entry.IdLocked ? entry.discoveredIn : "Not referenced from Lua in this content folder."),
                EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(entry.itemId))
            {
                var statusRect = new Rect(card.xMax - 20f, card.y + 4f, 16f, 16f);
                var status = StatusOf(entry);
                if (status.Key)
                {
                    var warnIcon = EditorGUIUtility.IconContent("console.warnicon.sml");
                    GUI.Label(statusRect, new GUIContent(warnIcon != null ? warnIcon.image : null, status.Value));
                }
                else
                {
                    var checkStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
                    checkStyle.normal.textColor = new Color(0.35f, 0.82f, 0.4f);
                    GUI.Label(statusRect, new GUIContent("✓", status.Value), checkStyle);
                }
            }

            var removeRect = new Rect(card.xMax - 56f, card.y + CardHeight - 22f, 50f, 16f);
            using (new EditorGUI.DisabledScope(entry.IdLocked))
            {
                if (GUI.Button(removeRect, new GUIContent("Remove", entry.IdLocked
                        ? "This item is referenced from your Lua. Remove the call (or @item line) and rescan."
                        : "Remove this item card. (It is not deleted from the items catalog — use dreampark_items_delete.)"),
                        EditorStyles.miniButton))
                    removeIndex = index;
            }

            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUI.indentLevel = prevIndent;
        }

        /// Fire-and-forget push that rides Compile & Upload, like
        /// ContentUploaderPanel.PushBadgesSilently: never fails or delays an
        /// upload (items can be re-pushed from the panel in one click).
        /// <paramref name="idForUpload"/> is the backend id (may be a Beta
        /// twin); <paramref name="sourceContentId"/> the local folder.
        public static void PushSilently(string idForUpload, string sourceContentId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(idForUpload)) return;
                string local = string.IsNullOrEmpty(sourceContentId) ? idForUpload : sourceContentId;
                var toPush = ItemStore.Load(local);
                if (toPush == null || toPush.Count == 0) return;
                ItemUploader.UploadAll(idForUpload, toPush, interactive: false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Items] upload skipped: " + e.Message);
            }
        }
    }
}
#endif
