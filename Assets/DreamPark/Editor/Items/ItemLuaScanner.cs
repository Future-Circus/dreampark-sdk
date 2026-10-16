#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DreamPark.Badges;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Items
{
    // Finds every item id a content package's own Lua uses, so the Content
    // Uploader's Items section can prefill (and lock) the id instead of asking
    // the developer to retype a string their game already passes. The item
    // twin of BadgeLuaScanner, and it shares that scanner's grammar on purpose
    // (comment stripper, @var parser delegation, one-hop alias resolution):
    // a second copy of those is how two scanners start disagreeing.
    //
    // ── The shapes ───────────────────────────────────────────────────────────
    //  1. LITERAL      dp.profile.awardItem("potion") / awardUniqueItem('cr_fox', …)
    //  2. @var DEFAULT -- @var itemId string "potion"  …  awardItem(itemId)
    //  3. INSPECTOR    the serialized value of that @var on a LuaBehaviour/EasyLua
    //  4. ANNOTATION   -- @item cr_fox "Fox"
    //     For ids built at runtime ('cr_' .. species): no text scan can read
    //     them, so the developer declares them in a comment, one per line,
    //     with an optional display name that prefills an empty Title.
    //
    // ── Which calls ──────────────────────────────────────────────────────────
    //  AWARDS (awardItem, awardUniqueItem, rewardUniqueItem) are the ones that
    //  must resolve: an award whose id the scan can't read is reported as
    //  UNRESOLVED so the developer knows to add an @item line (unless that
    //  script already declares @item lines).
    //  READS / REMOVES (getItem, getItems, hasItem, removeItem) name an id too,
    //  but routinely take an INSTANCE id held in a variable
    //  (removeItem(critter.instanceId)), so only their literals and @vars count
    //  and anything else is ignored silently rather than reported as noise.
    //  updateItem always takes an instance id and is not scanned.
    //
    //  A unique award (awardUniqueItem / rewardUniqueItem, or awardItem("x", n,
    //  {…}|true)) marks the id `unique`: such an item needs the catalog's
    //  "Stacks" left ON, or the server treats it as a singleton and drops the
    //  per-instance metadata (ItemAwardCheck warns).
    //
    // Same deliberate limits as BadgeLuaScanner: prefabs only for shape 3, and
    // no guessing at non-literal expressions.
    public static class ItemLuaScanner
    {
        private static readonly Regex CallRegex = new Regex(
            @"\b(awardItem|awardUniqueItem|rewardUniqueItem|getItem|getItems|hasItem|removeItem)\s*\(\s*([^,()]*)",
            RegexOptions.Compiled);

        // awardItem("x", n, {  or  awardItem("x", n, true   → a unique award.
        private static readonly Regex UniqueAwardItemRegex = new Regex(
            @"\bawardItem\s*\(\s*(?:""([^""]*)""|'([^']*)')\s*,[^,()]*,\s*(?:\{|true\b)",
            RegexOptions.Compiled);

        // -- @item <id> ["Display Name"]   (read off the RAW source: it's a comment)
        private static readonly Regex AnnotationRegex = new Regex(
            @"--\s*@item\s+([A-Za-z0-9_-]+)(?:[ \t]+""([^""\r\n]*)"")?",
            RegexOptions.Compiled);

        private static bool IsAward(string fn)
        {
            return fn == "awardItem" || fn == "awardUniqueItem" || fn == "rewardUniqueItem";
        }

        private static bool IsUniqueAward(string fn)
        {
            return fn == "awardUniqueItem" || fn == "rewardUniqueItem";
        }

        public class Discovery
        {
            public string itemId;
            public ItemStore.IdSource source;
            public string scriptPath;
            public string prefabPath;   // only for InspectorValue
            public string varName;      // only for the @var-backed shapes
            public string displayName;  // only for LuaAnnotation
            public bool unique;

            public string Where()
            {
                switch (source)
                {
                    case ItemStore.IdSource.LuaLiteral:
                        return $"Found as a literal in {Short(scriptPath)}";
                    case ItemStore.IdSource.LuaVarDefault:
                        return $"Found as the '@var {varName}' default in {Short(scriptPath)}";
                    case ItemStore.IdSource.InspectorValue:
                        return $"Found as '{varName}' on {Short(prefabPath)} (script {Short(scriptPath)})";
                    case ItemStore.IdSource.LuaAnnotation:
                        return $"Declared with '-- @item {itemId}' in {Short(scriptPath)}";
                    default:
                        return "";
                }
            }

            private static string Short(string p)
            {
                return string.IsNullOrEmpty(p) ? "(unknown)" : Path.GetFileName(p);
            }
        }

        public class Unresolved
        {
            public string scriptPath;
            public string expression;
        }

        public class Result
        {
            public List<Discovery> discoveries = new List<Discovery>();
            public List<Unresolved> unresolved = new List<Unresolved>();
            public int scriptsScanned;
            public int prefabsScanned;

            public Discovery Find(string itemId)
            {
                return discoveries.FirstOrDefault(d => d != null && string.Equals(d.itemId, itemId, StringComparison.Ordinal));
            }
        }

        public static Result Scan(string contentId)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(contentId)) return result;

            string contentRoot = ItemStore.ContentFolder + "/" + contentId;
            if (!AssetDatabase.IsValidFolder(contentRoot)) return result;

            var varsNeedingInspectorValues = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
            var seen = new Dictionary<string, Discovery>(StringComparer.Ordinal);

            foreach (string scriptPath in FindLuaScripts(contentRoot))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(scriptPath);
                if (asset == null) continue;

                string source;
                try { source = asset.text; }
                catch { continue; }
                if (string.IsNullOrEmpty(source)) continue;

                result.scriptsScanned++;
                ScanSource(source, scriptPath, result, seen, varsNeedingInspectorValues);
            }

            if (varsNeedingInspectorValues.Count > 0)
                ScanPrefabsForInspectorValues(contentRoot, varsNeedingInspectorValues, result, seen);

            return result;
        }

        /// <summary>Text-only pass over one script (shapes 1, 2 and 4).
        /// Public so a test (or a tool) can scan a string without assets.</summary>
        public static Result ScanText(string source, string scriptPath = "inline.lua")
        {
            var result = new Result();
            ScanSource(source ?? "", scriptPath, result, new Dictionary<string, Discovery>(StringComparer.Ordinal),
                new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal));
            return result;
        }

        private static void ScanSource(string source, string scriptPath, Result result,
            Dictionary<string, Discovery> seen, Dictionary<string, Dictionary<string, bool>> varsNeedingInspectorValues)
        {
            // Annotations and @var lines live INSIDE comments, so read them off
            // the raw text before the stripper blanks them. A script that
            // declares its ids with @item lines has answered "which ids does
            // this runtime-built award produce?", so its unreadable award
            // arguments are not reported again.
            bool declaresItems = false;
            foreach (Match m in AnnotationRegex.Matches(source))
            {
                declaresItems = true;
                string id = ItemStore.SanitizeId(m.Groups[1].Value);
                if (id.Length == 0) continue;
                AddDiscovery(result, seen, new Discovery
                {
                    itemId = id,
                    source = ItemStore.IdSource.LuaAnnotation,
                    scriptPath = scriptPath,
                    displayName = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null,
                });
            }

            var declaredStringVars = BadgeLuaScanner.ParseStringVarDefaults(source);
            string stripped = BadgeLuaScanner.StripComments(source);
            var aliases = BadgeLuaScanner.BuildAliasMap(stripped);

            var uniqueLiterals = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in UniqueAwardItemRegex.Matches(stripped))
            {
                string id = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                if (id.Length > 0) uniqueLiterals.Add(id);
            }

            foreach (Match m in CallRegex.Matches(stripped))
            {
                string fn = m.Groups[1].Value;
                string arg = m.Groups[2].Value.Trim();
                bool award = IsAward(fn);
                if (arg.Length == 0) continue;            // getItems() — no id
                if (arg.StartsWith("{")) continue;        // getItems({ type = … }) — a filter table

                var lit = BadgeLuaScanner.StringLiteralRegex.Match(arg);
                if (lit.Success)
                {
                    string id = (lit.Groups[1].Success ? lit.Groups[1].Value : lit.Groups[2].Value).Trim();
                    if (id.Length == 0) continue;
                    AddDiscovery(result, seen, new Discovery
                    {
                        itemId = id,
                        source = ItemStore.IdSource.LuaLiteral,
                        scriptPath = scriptPath,
                        unique = IsUniqueAward(fn) || uniqueLiterals.Contains(id),
                    });
                    continue;
                }

                if (!BadgeLuaScanner.IdentifierRegex.IsMatch(arg))
                {
                    if (award && !declaresItems) AddUnresolved(result, scriptPath, arg);
                    continue;
                }

                string root = BadgeLuaScanner.ResolveAlias(arg, aliases);
                string defaultValue;
                if (!declaredStringVars.TryGetValue(root, out defaultValue))
                {
                    if (award && !declaresItems) AddUnresolved(result, scriptPath, arg);
                    continue;
                }

                if (!string.IsNullOrEmpty(defaultValue))
                {
                    AddDiscovery(result, seen, new Discovery
                    {
                        itemId = defaultValue,
                        source = ItemStore.IdSource.LuaVarDefault,
                        scriptPath = scriptPath,
                        varName = root,
                        unique = IsUniqueAward(fn),
                    });
                }

                Dictionary<string, bool> names;
                if (!varsNeedingInspectorValues.TryGetValue(scriptPath, out names))
                {
                    names = new Dictionary<string, bool>(StringComparer.Ordinal);
                    varsNeedingInspectorValues[scriptPath] = names;
                }
                bool wasUnique;
                names.TryGetValue(root, out wasUnique);
                names[root] = wasUnique || IsUniqueAward(fn);
            }
        }

        private static void ScanPrefabsForInspectorValues(
            string contentRoot,
            Dictionary<string, Dictionary<string, bool>> varsByScript,
            Result result,
            Dictionary<string, Discovery> seen)
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot });
            foreach (string guid in guids)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(prefabPath)) continue;
                if (prefabPath.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;

                result.prefabsScanned++;

                foreach (var mb in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    var injectable = mb as ILuaInjectable;
                    if (injectable == null) continue;

                    var script = injectable.luaScript;
                    if (script == null) continue;

                    string scriptPath = AssetDatabase.GetAssetPath(script);
                    Dictionary<string, bool> wanted;
                    if (string.IsNullOrEmpty(scriptPath)
                        || !varsByScript.TryGetValue(scriptPath, out wanted)) continue;

                    var strings = injectable.stringInjections;
                    if (strings == null) continue;

                    foreach (var inj in strings)
                    {
                        if (inj == null || string.IsNullOrEmpty(inj.name)) continue;
                        bool unique;
                        if (!wanted.TryGetValue(inj.name, out unique)) continue;

                        string value = (inj.value ?? "").Trim();
                        if (value.Length == 0) continue;

                        AddDiscovery(result, seen, new Discovery
                        {
                            itemId = value,
                            source = ItemStore.IdSource.InspectorValue,
                            scriptPath = scriptPath,
                            prefabPath = prefabPath,
                            varName = inj.name,
                            unique = unique,
                        });
                    }
                }
            }
        }

        // First writer wins on attribution (scripts before prefabs); the unique
        // flag is OR-ed across every sighting of the id.
        private static void AddDiscovery(Result result, Dictionary<string, Discovery> seen, Discovery d)
        {
            Discovery first;
            if (seen.TryGetValue(d.itemId, out first))
            {
                if (d.unique) first.unique = true;
                if (string.IsNullOrEmpty(first.displayName) && !string.IsNullOrEmpty(d.displayName))
                    first.displayName = d.displayName;
                return;
            }
            seen[d.itemId] = d;
            result.discoveries.Add(d);
        }

        private static void AddUnresolved(Result result, string scriptPath, string expression)
        {
            if (result.unresolved.Any(u => u.scriptPath == scriptPath && u.expression == expression)) return;
            result.unresolved.Add(new Unresolved { scriptPath = scriptPath, expression = expression });
        }

        private static IEnumerable<string> FindLuaScripts(string contentRoot)
        {
            return AssetDatabase.FindAssets("t:TextAsset", new[] { contentRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Where(p => p.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                .Where(p => p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal);
        }
    }
}
#endif
