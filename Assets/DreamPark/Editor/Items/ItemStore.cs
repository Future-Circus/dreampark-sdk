#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DreamPark.Items
{
    // Per-content-package record of the ITEMS this game defines — the items
    // catalog twin of BadgeStore, and deliberately built the same way.
    //
    // Stored at Assets/Content/{contentId}/.items.json
    //
    // Dot-prefixed so Unity never imports it (no .meta, no GUID, never in a
    // bundle) while git still tracks it: the whole team shares one item list.
    //
    // A DRAFT, not the source of truth. The backend's `items` collection is
    // authoritative once pushed (Content Uploader → Items → Push Items to
    // Portal, or silently with every Compile & Upload); this is the buffer that
    // survives a domain reload, and the record of which ids came from Lua.
    //
    // An item is a CATALOG entry (name, icon, type, rarity, stacking). What a
    // player owns lives in their profile inventory; a UNIQUE item (one minted
    // with metadata, e.g. each caught creature) is many inventory rows that all
    // point at this one catalog entry.
    public static class ItemStore
    {
        private const int kCurrentVersion = 1;
        private const string kFileName = ".items.json";
        public const string ContentFolder = "Assets/Content";

        // The backend's rarity vocabulary (views/admin/content-catalog-entries.ejs).
        // "" = none. Anything else is still sent verbatim — the server stores
        // the string — but the panel offers only these.
        public static readonly string[] Rarities = { "", "common", "uncommon", "rare", "epic", "legendary" };

        // Where an item id came from. Only Manual is editable in the panel — an
        // id found in the developer's own Lua is locked so the panel's id and
        // the string their game passes to dp.profile.awardItem cannot drift.
        public enum IdSource
        {
            Manual = 0,          // typed into the panel
            LuaLiteral = 1,      // dp.profile.awardItem("literal_id")
            LuaVarDefault = 2,   // -- @var itemId string "default_id"
            InspectorValue = 3,  // the value serialized on a LuaBehaviour/EasyLua
            LuaAnnotation = 4,   // -- @item cr_fox "Fox"  (ids built at runtime)
        }

        [Serializable]
        public class Entry
        {
            public string itemId = "";
            public string name = "";
            public string description = "";
            public string type = "";
            public string rarity = "";
            // Mirrors the catalog's `stackable`. TRUE (the backend default) is
            // also what UNIQUE items need: a non-stacking catalog entry is a
            // singleton, and the server ignores award metadata for it.
            public bool stackable = true;
            public string iconAssetPath = "";
            public int idSource = (int)IdSource.Manual;
            // "Found in …" — the tooltip on a locked id. Never identity.
            public string discoveredIn = "";
            // The scan saw a unique award (awardUniqueItem, or awardItem with
            // metadata) for this id. Drives the "Stacks must stay on" warning.
            public bool awardedUnique;

            public IdSource Source
            {
                get { return (IdSource)idSource; }
                set { idSource = (int)value; }
            }

            public bool IdLocked
            {
                get { return Source != IdSource.Manual; }
            }

            public bool IsBlank
            {
                get
                {
                    return string.IsNullOrEmpty(itemId) && string.IsNullOrEmpty(name)
                        && string.IsNullOrEmpty(description) && string.IsNullOrEmpty(type)
                        && string.IsNullOrEmpty(rarity) && string.IsNullOrEmpty(iconAssetPath);
                }
            }
        }

        [Serializable]
        private class FileModel
        {
            public int version = kCurrentVersion;
            public List<Entry> entries = new List<Entry>();
        }

        public static string PathFor(string contentId)
        {
            return $"{ContentFolder}/{contentId}/{kFileName}";
        }

        public static List<Entry> Load(string contentId)
        {
            return LoadModel(contentId).entries;
        }

        public static void Save(string contentId, List<Entry> entries)
        {
            if (string.IsNullOrEmpty(contentId)) return;

            string path = PathFor(contentId);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var model = new FileModel
                {
                    version = kCurrentVersion,
                    // Stable order → readable diffs.
                    entries = (entries ?? new List<Entry>())
                        .Where(e => e != null && !e.IsBlank)
                        .OrderBy(e => e.itemId ?? "", StringComparer.Ordinal)
                        .ToList(),
                };

                // Never create an empty draft just because the panel looked.
                if (model.entries.Count == 0 && !File.Exists(path)) return;

                string json = JsonUtility.ToJson(model, true);
                // Don't touch the file (or its mtime) when nothing changed.
                if (File.Exists(path) && File.ReadAllText(path) == json) return;
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not write {path}: {e.Message}");
            }
        }

        private static FileModel LoadModel(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return new FileModel();

            string path = PathFor(contentId);
            try
            {
                if (!File.Exists(path)) return new FileModel();

                var model = JsonUtility.FromJson<FileModel>(File.ReadAllText(path));
                if (model == null) return new FileModel();
                if (model.entries == null) model.entries = new List<Entry>();
                model.entries.RemoveAll(e => e == null);

                if (model.version > kCurrentVersion)
                {
                    Debug.LogWarning(
                        $"[DreamPark] {path} was written by a newer SDK (schema v{model.version}). " +
                        "Reading it anyway; update the SDK if items behave oddly.");
                }

                return model;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read {path}: {e.Message}. Treating as empty.");
                return new FileModel();
            }
        }

        // Merges what ItemLuaScanner found into the saved draft. Same rules as
        // BadgeStore.Merge:
        //   • a discovered id with no saved entry becomes a new locked card;
        //   • a discovered id matching a MANUAL entry locks it and keeps the
        //     title/description/type/rarity/icon the developer typed;
        //   • a previously locked entry no longer discovered falls back to
        //     Manual instead of disappearing (it may exist on the backend).
        // Match is by itemId, case-sensitively (the backend doesn't fold case).
        // An @item annotation's display name fills an EMPTY title only.
        public static List<Entry> Merge(List<Entry> saved, IReadOnlyList<ItemLuaScanner.Discovery> discovered)
        {
            var result = new List<Entry>();
            var byId = new Dictionary<string, Entry>(StringComparer.Ordinal);

            foreach (var e in saved ?? new List<Entry>())
            {
                if (e == null) continue;
                if (e.IdLocked)
                {
                    e.Source = IdSource.Manual;
                    e.discoveredIn = "";
                }
                e.awardedUnique = false;
                result.Add(e);
                if (!string.IsNullOrEmpty(e.itemId) && !byId.ContainsKey(e.itemId))
                    byId[e.itemId] = e;
            }

            foreach (var d in discovered ?? new List<ItemLuaScanner.Discovery>())
            {
                if (d == null || string.IsNullOrEmpty(d.itemId)) continue;

                Entry entry;
                if (!byId.TryGetValue(d.itemId, out entry))
                {
                    entry = new Entry { itemId = d.itemId };
                    byId[d.itemId] = entry;
                    result.Add(entry);
                }
                entry.Source = d.source;
                entry.discoveredIn = d.Where();
                if (d.unique) entry.awardedUnique = true;
                if (string.IsNullOrEmpty(entry.name) && !string.IsNullOrEmpty(d.displayName))
                    entry.name = d.displayName;
            }

            return result
                .OrderBy(e => e.IdLocked ? 0 : 1)
                .ThenBy(e => e.itemId ?? "", StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Same sanitizer the backend applies to an entry id
        /// (admin.content.routes.js sanitizeEntryId): [A-Za-z0-9_-], ≤ 80.</summary>
        public static string SanitizeId(string raw)
        {
            var s = (raw ?? "").Trim();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
            var r = sb.ToString();
            return r.Length > 80 ? r.Substring(0, 80) : r;
        }
    }
}
#endif
