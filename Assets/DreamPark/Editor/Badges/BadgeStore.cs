#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Badges
{
    // Per-content-package record of the badges this game defines.
    //
    // Stored at Assets/Content/{contentId}/.badges.json
    //
    // Dot-prefixed for the same reason PreUploadIgnoreStore and
    // PreviewMetadataStore are: Unity's asset pipeline ignores files whose name
    // starts with a dot, so no .meta is emitted, no GUID is minted, and the file
    // can never be swept into an addressable bundle. It is author-time metadata
    // that has no business shipping to the runtime — but it IS an ordinary file
    // on disk, so git tracks it and the whole team shares one badge list instead
    // of each person retyping titles the backend already has.
    //
    // This file is a DRAFT, not the source of truth. The backend's `badges`
    // collection is authoritative once the developer uploads; this is the local
    // buffer that survives a domain reload between typing a title and hitting
    // upload, and the record of which ids came from Lua rather than by hand.
    public static class BadgeStore
    {
        private const int kCurrentVersion = 1;
        private const string kFileName = ".badges.json";
        public const string ContentFolder = "Assets/Content";

        // Where a badge id came from. Only Manual is editable in the panel — an
        // id the scanner found in the developer's own Lua is locked, because the
        // whole point of the scan is that the string in the panel and the string
        // the game passes to dp.profile.awardBadge cannot drift apart.
        public enum IdSource
        {
            Manual = 0,          // typed into the panel
            LuaLiteral = 1,      // dp.profile.awardBadge("literal_id")
            LuaVarDefault = 2,   // -- @var badgeId string "default_id"
            InspectorValue = 3,  // the value serialized on a LuaBehaviour/EasyLua
        }

        [Serializable]
        public class Entry
        {
            public string badgeId = "";
            public string name = "";
            public string description = "";
            // Asset path of the icon, not a GUID: this file is read by humans in
            // diffs as often as by the panel, and an icon is cheap to re-pick if
            // someone moves it. (Contrast PreUploadIgnoreStore, which keys on
            // GUID because an ignore surviving a rename is the whole point.)
            public string iconAssetPath = "";
            public int idSource = (int)IdSource.Manual;
            // Human-readable "found in Assets/Content/X/Scripts/y.lua.txt" —
            // rendered as the tooltip on a locked id field. Never identity.
            public string discoveredIn = "";

            public IdSource Source
            {
                get { return (IdSource)idSource; }
                set { idSource = (int)value; }
            }

            public bool IdLocked
            {
                get { return Source != IdSource.Manual; }
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
                    // Stable ordering so the file produces readable diffs instead
                    // of reshuffling every time the panel repaints and saves.
                    entries = (entries ?? new List<Entry>())
                        .Where(e => e != null && !(string.IsNullOrEmpty(e.badgeId)
                                                   && string.IsNullOrEmpty(e.name)
                                                   && string.IsNullOrEmpty(e.description)
                                                   && string.IsNullOrEmpty(e.iconAssetPath)))
                        .OrderBy(e => e.badgeId ?? "", StringComparer.Ordinal)
                        .ToList(),
                };

                File.WriteAllText(path, JsonUtility.ToJson(model, true));
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

                if (model.version > kCurrentVersion)
                {
                    // Written by a newer SDK. Read what we can rather than
                    // refusing — a badge draft is advisory, and the backend holds
                    // the real catalog, so the worst case is retyping a title.
                    Debug.LogWarning(
                        $"[DreamPark] {path} was written by a newer SDK (schema v{model.version}). " +
                        "Reading it anyway; update the SDK if badges behave oddly.");
                }

                return model;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read {path}: {e.Message}. Treating as empty.");
                return new FileModel();
            }
        }

        // Merges what the Lua scanner found into the saved draft.
        //
        // Match is by badgeId, case-sensitively — the backend's sanitizeEntryId
        // does not fold case, so "GoldStar" and "goldstar" really are two badges
        // and pretending otherwise would silently merge them.
        //
        // Rules:
        //   • A discovered id with no saved entry becomes a new locked card.
        //   • A discovered id that matches a MANUAL entry promotes that entry to
        //     locked and keeps the title/description/icon the developer typed —
        //     they hand-authored a badge and then wired it up, which is the
        //     common order, and losing their copy for it would be hostile.
        //   • A saved entry that is no longer discovered but was previously
        //     locked falls BACK to Manual rather than disappearing. The badge
        //     exists on the backend; deleting the local card because someone
        //     commented out one Lua line would silently orphan it.
        public static List<Entry> Merge(List<Entry> saved, IReadOnlyList<BadgeLuaScanner.Discovery> discovered)
        {
            var result = new List<Entry>();
            var byId = new Dictionary<string, Entry>(StringComparer.Ordinal);

            foreach (var e in saved ?? new List<Entry>())
            {
                if (e == null) continue;
                // Anything that was locked and isn't rediscovered this pass drops
                // back to editable; the loop below re-locks whatever is still found.
                if (e.IdLocked)
                {
                    e.Source = IdSource.Manual;
                    e.discoveredIn = "";
                }
                result.Add(e);
                if (!string.IsNullOrEmpty(e.badgeId) && !byId.ContainsKey(e.badgeId))
                    byId[e.badgeId] = e;
            }

            foreach (var d in discovered ?? new List<BadgeLuaScanner.Discovery>())
            {
                if (d == null || string.IsNullOrEmpty(d.badgeId)) continue;

                Entry entry;
                if (!byId.TryGetValue(d.badgeId, out entry))
                {
                    entry = new Entry { badgeId = d.badgeId };
                    byId[d.badgeId] = entry;
                    result.Add(entry);
                }
                entry.Source = d.source;
                entry.discoveredIn = d.Where();
            }

            return result
                .OrderBy(e => e.IdLocked ? 0 : 1)
                .ThenBy(e => e.badgeId ?? "", StringComparer.Ordinal)
                .ToList();
        }
    }
}
#endif
