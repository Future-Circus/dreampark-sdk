#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using Defective.JSON;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Plain data for everything the Asset Browser reads off /api/assets.
    ///
    /// No UnityEngine types, no scene references, no importer calls — this is
    /// the TexturePlan.cs of this feature. Keeping it inert means the parsing
    /// can be reasoned about (and eventually tested) without an editor, and it
    /// means the window can hold a page of these across a domain reload
    /// without dragging live objects along with it.
    /// </summary>
    [Serializable]
    public class DreamieFile
    {
        public string url;
        public string filename;
        public string mime;
        /// <summary>
        /// What the publisher called this member — "glb", "front", "left".
        /// Dreamie sets it from the companion's format or view, so it is the
        /// most reliable way to identify an alternate format; the filename is
        /// the fallback.
        /// </summary>
        public string label;

        public static DreamieFile FromJson(JSONObject j)
        {
            if (j == null) return null;
            return new DreamieFile
            {
                url = j.GetField("url")?.stringValue,
                filename = j.GetField("filename")?.stringValue,
                mime = j.GetField("mime")?.stringValue,
                label = j.GetField("label")?.stringValue,
            };
        }
    }

    [Serializable]
    public class DreamieAsset
    {
        public string id;
        public string name;
        public string type;          // model | render | rig | texture | sfx | music | script | doc | other
        public string kind;
        public string description;
        public List<string> tags = new List<string>();

        public string fileUrl;       // the PRIMARY artifact
        public string filename;
        public string mime;
        public long bytes;

        public string previewUrl;    // grid thumbnail
        public string displayUrl;    // larger, for the detail drawer

        public List<DreamieFile> files = new List<DreamieFile>();
        public int fileCount = 1;

        public string assetJobId;
        public string creatorId;
        public string creatorName;

        public int downloads;
        public long createdAtMs;
        public bool isFavorite;

        /* ── selection + local state, owned by the window ──────────────── */

        /// <summary>
        /// Ticked in the grid. Lives ON THE ROW rather than in a separate
        /// HashSet, the same way TextureOptimizerWindow does it — rows survive
        /// re-filtering, so a selection made under one search term persists
        /// when the term changes, for free and without a second structure to
        /// keep in sync.
        /// </summary>
        public bool selected;

        /// <summary>Already in this project (matched by assetId in the folder index).</summary>
        public bool alreadyImported;

        /// <summary>Where it landed, when alreadyImported. Null otherwise.</summary>
        public string importedAtPath;

        public static DreamieAsset FromJson(JSONObject j)
        {
            if (j == null) return null;
            var a = new DreamieAsset
            {
                id = j.GetField("id")?.stringValue,
                name = j.GetField("name")?.stringValue ?? "",
                type = j.GetField("type")?.stringValue ?? "other",
                kind = j.GetField("kind")?.stringValue,
                description = j.GetField("description")?.stringValue ?? "",
                fileUrl = j.GetField("fileUrl")?.stringValue,
                filename = j.GetField("filename")?.stringValue,
                mime = j.GetField("mime")?.stringValue,
                previewUrl = j.GetField("previewUrl")?.stringValue,
                displayUrl = j.GetField("displayUrl")?.stringValue,
                assetJobId = j.GetField("assetJobId")?.stringValue,
                creatorId = j.GetField("creatorId")?.stringValue,
                creatorName = j.GetField("creatorName")?.stringValue,
                downloads = j.GetField("downloads")?.intValue ?? 0,
                isFavorite = j.GetField("isFavorite")?.boolValue ?? false,
                fileCount = j.GetField("fileCount")?.intValue ?? 1,
            };

            // Defective.JSON has no long accessor; bytes and epoch-millis both
            // exceed int range in practice, so read them as double and cast.
            var bytesField = j.GetField("bytes");
            if (bytesField != null) a.bytes = (long)bytesField.doubleValue;
            var createdField = j.GetField("createdAtMs");
            if (createdField != null) a.createdAtMs = (long)createdField.doubleValue;

            var tags = j.GetField("tags");
            if (tags != null && tags.type == JSONObject.Type.Array && tags.list != null)
                foreach (var t in tags.list)
                    if (!string.IsNullOrEmpty(t?.stringValue)) a.tags.Add(t.stringValue);

            var files = j.GetField("files");
            if (files != null && files.type == JSONObject.Type.Array && files.list != null)
                foreach (var f in files.list)
                {
                    var parsed = DreamieFile.FromJson(f);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.url)) a.files.Add(parsed);
                }

            return a;
        }

        /* ── format resolution ─────────────────────────────────────────────
         *
         * An asset's formats are spread across two places and the split is not
         * arbitrary: the PRIMARY is whatever Dreamie decided goes into a game,
         * and the companions are everything else that belongs to the same
         * mesh. For a mesh that is normally FBX primary + GLB companion, but
         * the publisher explicitly falls back to shipping the GLB as primary
         * when the FBX conversion failed — so "the FBX is fileUrl" is a
         * usually-true assumption, not a rule, and assuming it is how you end
         * up handing Unity a GLB named .fbx.
         *
         * Hence: ask by format, never by position.
         * ─────────────────────────────────────────────────────────────── */

        private static string ExtOf(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return "";
            int dot = filename.LastIndexOf('.');
            return dot < 0 ? "" : filename.Substring(dot + 1).ToLowerInvariant();
        }

        /// <summary>
        /// The download URL for a given format, or null when the asset does
        /// not ship one. Checks the primary first, then companions by
        /// filename extension, then by publisher label.
        /// </summary>
        public string UrlForFormat(string format, out string resolvedFilename)
        {
            resolvedFilename = null;
            string want = (format ?? "").ToLowerInvariant().TrimStart('.');
            if (want.Length == 0) return null;

            if (ExtOf(filename) == want && !string.IsNullOrEmpty(fileUrl))
            {
                resolvedFilename = filename;
                return fileUrl;
            }
            foreach (var f in files)
            {
                if (f == null || string.IsNullOrEmpty(f.url)) continue;
                bool match = ExtOf(f.filename) == want
                             || string.Equals(f.label, want, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                resolvedFilename = string.IsNullOrEmpty(f.filename) ? name + "." + want : f.filename;
                return f.url;
            }
            return null;
        }

        public string UrlForFormat(string format) => UrlForFormat(format, out _);

        /// <summary>True when this asset can actually be imported as a model.</summary>
        public bool HasFbx => !string.IsNullOrEmpty(UrlForFormat("fbx"));

        /// <summary>
        /// The GLB, recorded into provenance so a later Make Item can hand the
        /// backend a URL instead of exporting geometry out of Unity — which
        /// Unity cannot do without a glTF exporter it does not ship.
        /// </summary>
        public string GlbUrl => UrlForFormat("glb");

        public bool IsModel => string.Equals(type, "model", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(type, "rig", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A Dreamie batch — one Discord run that produced many assets.</summary>
    [Serializable]
    public class DreamieJob
    {
        public string id;
        public string name;
        public string status;
        public string createdByName;
        public int itemCount;
        public long createdAtMs;

        public static DreamieJob FromJson(JSONObject j)
        {
            if (j == null) return null;
            var job = new DreamieJob
            {
                id = j.GetField("id")?.stringValue,
                name = j.GetField("name")?.stringValue ?? "Untitled job",
                status = j.GetField("status")?.stringValue ?? "open",
                createdByName = j.GetField("createdByName")?.stringValue,
            };
            var created = j.GetField("createdAtMs");
            if (created != null) job.createdAtMs = (long)created.doubleValue;
            job.itemCount = j.GetField("counts")?.GetField("items")?.intValue ?? 0;
            return job;
        }

        /// <summary>What the folder on disk is called. See DreamieFolders.</summary>
        public string Label => string.IsNullOrEmpty(createdByName)
            ? name
            : $"{name}  ({createdByName})";
    }

    /// <summary>One creator, as offered by the /facets dropdown.</summary>
    [Serializable]
    public class DreamieCreator
    {
        public string creatorId;
        public string creatorName;
        public int count;

        public static DreamieCreator FromJson(JSONObject j)
        {
            if (j == null) return null;
            return new DreamieCreator
            {
                creatorId = j.GetField("creatorId")?.stringValue,
                // A creator with no recorded name is a real state, not an
                // error — hand-uploaded assets have no Discord identity. Show
                // the truth rather than inventing a person.
                creatorName = j.GetField("creatorName")?.stringValue ?? "Unknown",
                count = j.GetField("count")?.intValue ?? 0,
            };
        }
    }

    /// <summary>One page of the grid, plus the cursor that continues it.</summary>
    public class DreamiePage
    {
        public List<DreamieAsset> assets = new List<DreamieAsset>();
        public string nextCursor;
        /// <summary>
        /// The server fell back to an unordered query because a composite
        /// index is still building. It returns one honest page and no cursor;
        /// the window says so rather than showing a Load More that would
        /// repeat and skip rows.
        /// </summary>
        public bool degraded;
    }
}
#endif
