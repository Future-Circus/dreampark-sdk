#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// The Studio's working copy ⇄ the backend playlist store (repair Oct 20 2026, CONTRACT § 5: the backend is the
    /// source of truth; the repo .dreampark-playlists.json is an export). PURE: both directions go through
    /// PlaylistStudioJson, so every field either side does not know passes through.
    /// </summary>
    public static class PlaylistStudioSync
    {
        /// Fields the store stamps on every doc; never written into the repo export.
        public static readonly string[] ServerFields = { "rev", "updatedAt", "updatedBy", "createdAt", "baseRev" };

        /// GET …/playlists {block, playlists} (+ goals) → the working copy and the goal blocks (key → Lua).
        public static ContentPlaylistStore.Data FromBackend(JSONObject lists, out SortedDictionary<string, string> goals)
        {
            goals = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
            JSONObject block = lists?.GetField("block");
            var root = JSONObject.emptyObject;
            if (block != null && block.type == JSONObject.Type.Object)
                for (int i = 0; i < block.count; i++)
                    if (!ServerFields.Contains(block.keys[i])) root.AddField(block.keys[i], PlaylistStudioJson.DeepCopy(block.list[i]));
            if (!root.HasField("schemaVersion")) root.AddField("schemaVersion", 1);
            var playlists = JSONObject.emptyArray;
            foreach (JSONObject p in lists?.GetField("playlists")?.list ?? new List<JSONObject>())
            {
                if (p == null || p.type != JSONObject.Type.Object) continue;
                var copy = JSONObject.emptyObject;
                for (int i = 0; i < p.count; i++) if (!ServerFields.Contains(p.keys[i])) copy.AddField(p.keys[i], PlaylistStudioJson.DeepCopy(p.list[i]));
                playlists.Add(copy);
            }
            root.AddField("playlists", playlists);
            JSONObject g = lists?.GetField("goals");
            if (g != null && g.type == JSONObject.Type.Object)
                for (int i = 0; i < g.count; i++)
                {
                    string lua = g.list[i]?.GetField("lua")?.stringValue;
                    if (lua != null && PlaylistGoalStore.ValidKey(g.keys[i])) goals[g.keys[i]] = lua;
                }
            return PlaylistStudioJson.ReadData(root.Print());
        }

        /// The working copy (+ the title's goal blocks) → PUT …/playlists {block, playlists, goals}.
        public static JSONObject ToBackend(ContentPlaylistStore.Data data, string titleId, IDictionary<string, string> goals)
        {
            JSONObject root = PlaylistStudioJson.WriteData(data, titleId);
            var block = JSONObject.emptyObject;
            JSONObject playlists = JSONObject.emptyArray;
            for (int i = 0; i < root.count; i++)
            {
                if (root.keys[i] == "playlists") playlists = root.list[i];
                else if (!ServerFields.Contains(root.keys[i])) block.AddField(root.keys[i], root.list[i]);
            }
            var body = JSONObject.emptyObject;
            body.AddField("block", block);
            body.AddField("playlists", playlists);
            if (goals != null && goals.Count > 0)
            {
                var g = JSONObject.emptyObject;
                foreach (var kv in goals.OrderBy(k => k.Key, System.StringComparer.Ordinal))
                {
                    var doc = JSONObject.emptyObject;
                    doc.AddField("lua", kv.Value);
                    doc.AddField("source", PlaylistGoalStore.SourceOf(kv.Value));
                    g.AddField(kv.Key, doc);
                }
                body.AddField("goals", g);
            }
            return body;
        }
    }
}
#endif
