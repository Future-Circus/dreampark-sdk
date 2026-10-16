#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Playtest
{
    /// <summary>
    /// What the tester knows about the game before it walks in: the creator's
    /// design notes, every attraction/prop/player prefab with the scripts wired
    /// into it, the Lua source itself, and the package authoring files. Reading the
    /// code is how the tester forms hypotheses about how the game is SUPPOSED to
    /// work, which is what lets it tell a bug from a design choice.
    ///
    /// Creators steer it with an optional PLAYTEST.md in their content folder
    /// (rules, win conditions, known issues, what to focus on).
    /// </summary>
    public static class PlaytestBriefing
    {
        public const string NotesFileName = "PLAYTEST.md";
        const int SourceBudget = 120_000;
        const int PerFileCap = 24_000;

        public static List<string> ContentIds()
        {
            var ids = new List<string>();
            if (!Directory.Exists(ContentFolders.Root)) return ids;
            foreach (var dir in Directory.GetDirectories(ContentFolders.Root))
                ids.Add(Path.GetFileName(dir));
            // The creator's own game first; Sample last.
            return ids.OrderBy(i => ContentFolders.IsSample(i) ? 1 : 0).ThenBy(i => i).ToList();
        }

        public static string DefaultContentId()
        {
            var ids = ContentIds();
            string game = ContentFolders.GameFolderName();
            if (ids.Contains(game) && !ContentFolders.IsPlaceholder(game)) return game;
            return ids.FirstOrDefault(i => !ContentFolders.IsPlaceholder(i)) ?? game;
        }

        public static string Build(string contentId)
        {
            string folder = ContentFolders.Root + "/" + contentId;
            var sb = new StringBuilder();
            sb.Append("# Game briefing: ").Append(contentId).Append("\n\n");

            if (!AssetDatabase.IsValidFolder(folder))
            {
                sb.Append("(No content folder at ").Append(folder).Append(". Learn the game by observing it.)\n");
                return sb.ToString();
            }

            string notes = ReadIfExists(folder + "/" + NotesFileName) ?? ReadIfExists(folder + "/README.md");
            if (notes != null)
            {
                sb.Append("## Designer notes (from the creator — treat as the spec)\n").Append(Cap(notes, 12_000)).Append("\n\n");
            }
            else
            {
                sb.Append("## Designer notes\n(None. Infer intended gameplay from prefabs and scripts.)\n\n");
            }

            var scriptsUsed = new Dictionary<string, TextAsset>();
            sb.Append("## Prefabs\n");
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsVendored(path)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                string kind = prefab.GetComponent<AttractionTemplate>() != null ? "ATTRACTION"
                    : prefab.GetComponent<PropTemplate>() != null ? "PROP"
                    : prefab.GetComponent<PlayerRig>() != null ? "PLAYER RIG"
                    : prefab.GetComponent<LevelTemplate>() != null ? "LEVEL"
                    : prefab.name.Contains("Game Container") ? "GAME MANAGER"
                    : "prefab";
                sb.Append("- ").Append(kind).Append(" \"").Append(prefab.name).Append("\" (").Append(path).Append(")");

                var wired = new List<string>();
                foreach (var lb in prefab.GetComponentsInChildren<LuaBehaviour>(true))
                {
                    if (lb.luaScript == null) continue;
                    string where = lb.gameObject == prefab ? "root" : lb.gameObject.name;
                    wired.Add($"{lb.luaScript.name} on \"{where}\"{(lb.gameObject.activeSelf ? "" : " (inactive)")}");
                    scriptsUsed[AssetDatabase.GetAssetPath(lb.luaScript)] = lb.luaScript;
                }
                if (wired.Count > 0) sb.Append("\n  scripts: ").Append(string.Join("; ", wired.Take(25))).Append(wired.Count > 25 ? "; …" : "");
                var colliders = prefab.GetComponentsInChildren<Collider>(true);
                int triggers = colliders.Count(c => c.isTrigger);
                if (triggers > 0) sb.Append("\n  trigger colliders: ").Append(triggers);
                // Tags are how many games tell a coin from an enemy (a collector filters
                // contacts by tag), so they are part of the rules.
                var tags = colliders.Select(c => c.tag).Where(t => t != "Untagged").Distinct().ToList();
                if (tags.Count > 0) sb.Append("\n  collider tags: ").Append(string.Join(", ", tags));
                sb.Append('\n');
            }
            sb.Append('\n');

            // Lua source: every script in the content folder, plus SDK scripts the
            // prefabs actually wire (e.g. the Sequence Start Button).
            var sources = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".lua.txt") && !IsVendored(path)) sources.Add(path);
            }
            foreach (var path in scriptsUsed.Keys)
                if (!sources.Contains(path)) sources.Add(path);

            sb.Append("## Lua scripts\n");
            int budget = SourceBudget;
            var omitted = new List<string>();
            foreach (var path in sources.OrderBy(p => p.StartsWith(folder) ? 0 : 1).ThenBy(p => p))
            {
                string text = ReadIfExists(path);
                if (text == null) continue;
                if (text.Length > budget) { omitted.Add(path); continue; }
                string body = Cap(text, PerFileCap);
                budget -= body.Length;
                sb.Append("### ").Append(path).Append("\n```lua\n").Append(body).Append("\n```\n");
            }
            if (omitted.Count > 0)
                sb.Append("(Not included for length — use read_script: ").Append(string.Join(", ", omitted)).Append(")\n");
            sb.Append('\n');

            var packageFiles = Directory.Exists(folder)
                ? Directory.GetFiles(folder, ".dreampark-*.json").Select(p => p.Replace('\\', '/')).ToList()
                : new List<string>();
            if (packageFiles.Count > 0)
            {
                sb.Append("## Package authoring (Arena / Sequence / Adventure)\n");
                foreach (var p in packageFiles)
                    sb.Append("### ").Append(Path.GetFileName(p)).Append("\n```json\n").Append(Cap(File.ReadAllText(p), 4_000)).Append("\n```\n");
            }
            return sb.ToString();
        }

        /// Reads a Lua script by name or path, restricted to the project's Assets.
        public static string ReadScript(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;
            string wanted = nameOrPath.Replace('\\', '/');
            if (wanted.StartsWith("Assets/") && wanted.EndsWith(".lua.txt") && !wanted.Contains("..") && File.Exists(wanted))
                return wanted + "\n" + File.ReadAllText(wanted);

            string stem = Path.GetFileName(wanted).Replace(".lua.txt", "").Replace(".lua", "");
            foreach (var guid in AssetDatabase.FindAssets(stem + " t:TextAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".lua.txt") && Path.GetFileName(path).StartsWith(stem))
                    return path + "\n" + File.ReadAllText(path);
            }
            return null;
        }

        static bool IsVendored(string path) => path.Contains("/ThirdParty/") || path.Contains("/ThirdPartyLocal/");

        static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        static string Cap(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n… (truncated)";
    }
}
#endif
