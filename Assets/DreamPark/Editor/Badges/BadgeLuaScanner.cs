#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Badges
{
    // Finds every badge id a content package's own Lua actually uses, so the
    // Content Uploader can prefill the id field instead of asking a developer to
    // retype a string that already exists in their game. A badge whose panel id
    // and whose dp.profile.awardBadge() id disagree is a badge that silently
    // never awards, and that is the failure this scanner exists to make
    // impossible.
    //
    // ── The three shapes, in the order they get harder ───────────────────────
    //
    //  1. LITERAL — dp.profile.awardBadge("gold_star")
    //     Read straight out of the script text.
    //
    //  2. @var DEFAULT — the shipped SDK sample (Samples/ProfileAPI/badge.lua.txt)
    //     does NOT pass a literal. It declares
    //         -- @var badgeId string "gold_star"
    //     and calls awardBadge(id) where `local id = badgeId`. So the scanner has
    //     to (a) parse the @var block, (b) follow one-hop local aliases, and (c)
    //     read the annotation's default. The @var grammar is not re-implemented
    //     here — LuaInjectionEditorGUI.ParseLuaVars is the shipped parser the
    //     Inspector itself uses, and a second copy of that regex would drift.
    //
    //  3. INSPECTOR VALUE — the default in the comment is usually "" and the real
    //     id is whatever the developer typed into the Inspector, which lives in
    //     the SERIALIZED StringInjection[] on the LuaBehaviour (or EasyLua) that
    //     references the script, not in the script text at all. We read that
    //     through the ILuaInjectable interface off the loaded prefab rather than
    //     by parsing prefab YAML — the interface is the supported surface and the
    //     YAML layout is not.
    //
    // ── Deliberate limits (see the report in the panel) ──────────────────────
    //
    //  • PREFABS ONLY. Shape 3 is resolved against prefabs under the content
    //    folder, which is what actually ships (Addressables bundles prefabs; a
    //    .unity scene under Assets/Content is an author-time test bed and is not
    //    packaged). A LuaBehaviour placed only in a scene is not scanned.
    //  • A non-literal, non-identifier argument — a concatenation, a table
    //    lookup, a function call — is reported as UNRESOLVED rather than guessed
    //    at. Guessing here would put a wrong id in a locked field, which is worse
    //    than an empty one the developer fills in themselves.
    public static class BadgeLuaScanner
    {
        // The four Lua entry points that name a badge id. Awards and removes are
        // the obvious ones; has/get are included because a game that only CHECKS
        // a badge in one script still names an id its developer has to define.
        //
        // internal, not private: BadgeAttributionScanner re-runs this exact
        // resolution PER CONTENT ROOT (to work out which Attraction/Prop/Player
        // actually awards a given id) and must not carry a second copy of this
        // grammar — a second copy is exactly how the four IsUserFacingRoot
        // copies ContentRootScanner's own header warns about drift.
        internal static readonly Regex CallRegex = new Regex(
            @"\b(?:awardBadge|removeBadge|hasBadge|getBadge)\s*\(\s*([^,()]*)",
            RegexOptions.Compiled);

        internal static readonly Regex StringLiteralRegex = new Regex(
            @"^(?:""([^""]*)""|'([^']*)')$", RegexOptions.Compiled);

        internal static readonly Regex IdentifierRegex = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        // `local id = badgeId` / `id = badgeId`. The RHS must be a BARE
        // identifier to end-of-statement: `shouldRemove = remove or false` is an
        // expression, not an alias, and following it would be a guess.
        private static readonly Regex AliasRegex = new Regex(
            @"^\s*(?:local\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\s*$",
            RegexOptions.Compiled);

        public class Discovery
        {
            public string badgeId;
            public BadgeStore.IdSource source;
            public string scriptPath;
            public string prefabPath;   // only for InspectorValue
            public string varName;      // only for the @var-backed shapes

            public string Where()
            {
                switch (source)
                {
                    case BadgeStore.IdSource.LuaLiteral:
                        return $"Found as a literal in {Short(scriptPath)}";
                    case BadgeStore.IdSource.LuaVarDefault:
                        return $"Found as the '@var {varName}' default in {Short(scriptPath)}";
                    case BadgeStore.IdSource.InspectorValue:
                        return $"Found as '{varName}' on {Short(prefabPath)} (script {Short(scriptPath)})";
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
        }

        public static Result Scan(string contentId)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(contentId)) return result;

            string contentRoot = BadgeStore.ContentFolder + "/" + contentId;
            if (!AssetDatabase.IsValidFolder(contentRoot)) return result;

            // scriptPath -> the set of @var names that reach a badge call in it.
            // Only populated for scripts that actually need the prefab pass, so a
            // package with nothing but literals never pays for it.
            var varsNeedingInspectorValues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string scriptPath in FindLuaScripts(contentRoot))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(scriptPath);
                if (asset == null) continue;

                string source;
                try { source = asset.text; }
                catch { continue; }
                if (string.IsNullOrEmpty(source)) continue;

                result.scriptsScanned++;

                // @var lines live INSIDE comments, so parse them off the raw text
                // before the stripper removes them.
                var declaredStringVars = ParseStringVarDefaults(source);

                string stripped = StripComments(source);
                var aliases = BuildAliasMap(stripped);

                foreach (Match m in CallRegex.Matches(stripped))
                {
                    string arg = m.Groups[1].Value.Trim();
                    if (arg.Length == 0) continue;

                    var lit = StringLiteralRegex.Match(arg);
                    if (lit.Success)
                    {
                        string id = lit.Groups[1].Success ? lit.Groups[1].Value : lit.Groups[2].Value;
                        id = id.Trim();
                        if (id.Length == 0) continue;   // awardBadge("") is a stub, not a badge
                        AddDiscovery(result, seen, new Discovery
                        {
                            badgeId = id,
                            source = BadgeStore.IdSource.LuaLiteral,
                            scriptPath = scriptPath,
                        });
                        continue;
                    }

                    if (!IdentifierRegex.IsMatch(arg))
                    {
                        AddUnresolved(result, scriptPath, arg);
                        continue;
                    }

                    string root = ResolveAlias(arg, aliases);
                    string defaultValue;
                    if (!declaredStringVars.TryGetValue(root, out defaultValue))
                    {
                        // A plain local the scanner cannot follow — e.g. an id
                        // built in a branch, or passed into a helper function.
                        AddUnresolved(result, scriptPath, arg);
                        continue;
                    }

                    // The @var default is a real badge id whenever the developer
                    // left one there. The SDK sample ships `""`, which is the
                    // "configure me in the Inspector" case, so an empty default
                    // is not a finding on its own — it is the reason for the
                    // prefab pass below.
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        AddDiscovery(result, seen, new Discovery
                        {
                            badgeId = defaultValue,
                            source = BadgeStore.IdSource.LuaVarDefault,
                            scriptPath = scriptPath,
                            varName = root,
                        });
                    }

                    HashSet<string> names;
                    if (!varsNeedingInspectorValues.TryGetValue(scriptPath, out names))
                    {
                        names = new HashSet<string>(StringComparer.Ordinal);
                        varsNeedingInspectorValues[scriptPath] = names;
                    }
                    names.Add(root);
                }
            }

            if (varsNeedingInspectorValues.Count > 0)
            {
                ScanPrefabsForInspectorValues(contentRoot, varsNeedingInspectorValues, result, seen);
            }

            return result;
        }

        // ── Shape 3: the serialized value on the component ──────────────────
        //
        // Reads through ILuaInjectable, which both LuaBehaviour and EasyLua
        // implement, so a badge wired on an EasyLua node is found on the same
        // pass. GetComponentsInChildren<MonoBehaviour>(true) rather than a typed
        // fetch because the interface is not a Unity type.
        private static void ScanPrefabsForInspectorValues(
            string contentRoot,
            Dictionary<string, HashSet<string>> varsByScript,
            Result result,
            HashSet<string> seen)
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
                    HashSet<string> wanted;
                    if (string.IsNullOrEmpty(scriptPath)
                        || !varsByScript.TryGetValue(scriptPath, out wanted)) continue;

                    var strings = injectable.stringInjections;
                    if (strings == null) continue;

                    foreach (var inj in strings)
                    {
                        if (inj == null || string.IsNullOrEmpty(inj.name)) continue;
                        if (!wanted.Contains(inj.name)) continue;

                        string value = (inj.value ?? "").Trim();
                        if (value.Length == 0) continue;

                        AddDiscovery(result, seen, new Discovery
                        {
                            badgeId = value,
                            source = BadgeStore.IdSource.InspectorValue,
                            scriptPath = scriptPath,
                            prefabPath = prefabPath,
                            varName = inj.name,
                        });
                    }
                }
            }
        }

        private static void AddDiscovery(Result result, HashSet<string> seen, Discovery d)
        {
            // First writer wins on a duplicate id. Scripts are walked before
            // prefabs, so a badge that is BOTH a @var default and an Inspector
            // value keeps the script attribution — but the id is identical
            // either way, and the id is the only thing that is load-bearing.
            if (!seen.Add(d.badgeId)) return;
            result.discoveries.Add(d);
        }

        private static void AddUnresolved(Result result, string scriptPath, string expression)
        {
            if (result.unresolved.Any(u => u.scriptPath == scriptPath && u.expression == expression)) return;
            result.unresolved.Add(new Unresolved { scriptPath = scriptPath, expression = expression });
        }

        private static IEnumerable<string> FindLuaScripts(string contentRoot)
        {
            // Lua ships as TextAssets. `.lua.txt` is the convention (Unity only
            // imports a bare `.lua` as a TextAsset in newer versions, and plenty
            // of content predates that), so accept both.
            return AssetDatabase.FindAssets("t:TextAsset", new[] { contentRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Where(p => p.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                .Where(p => p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        // Delegates the @var grammar to the shipped parser (the one the Lua
        // Inspector itself uses) and keeps only the string-typed declarations,
        // which is the only type a badge id can be.
        //
        // internal: shared with BadgeAttributionScanner for the same reason the
        // regexes above are.
        internal static Dictionary<string, string> ParseStringVarDefaults(string luaSource)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                // Entry is a struct, so there is nothing to null-check here.
                foreach (var e in LuaInjectionEditorGUI.ParseLuaVars(luaSource))
                {
                    if (string.IsNullOrEmpty(e.name)) continue;
                    if (e.type != LuaInjectionEditorGUI.VarType.String) continue;
                    map[e.name] = e.stringValue ?? "";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DreamPark] Badge scan: @var parse failed: " + ex.Message);
            }
            return map;
        }

        // internal: shared with BadgeAttributionScanner.
        internal static Dictionary<string, string> BuildAliasMap(string strippedSource)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in strippedSource.Split('\n'))
            {
                var m = AliasRegex.Match(rawLine.TrimEnd('\r'));
                if (!m.Success) continue;
                string lhs = m.Groups[1].Value;
                string rhs = m.Groups[2].Value;
                if (lhs == rhs) continue;
                // First binding wins. A name reassigned later in the file is
                // ambiguous, and picking the last one would be no more correct
                // than picking the first — but it would be less predictable.
                if (!aliases.ContainsKey(lhs)) aliases[lhs] = rhs;
            }
            return aliases;
        }

        // internal: shared with BadgeAttributionScanner.
        internal static string ResolveAlias(string name, Dictionary<string, string> aliases)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { name };
            string current = name;
            // Bounded: a cyclic alias chain (a = b; b = a) must terminate.
            for (int hop = 0; hop < 8; hop++)
            {
                string next;
                if (!aliases.TryGetValue(current, out next)) break;
                if (!visited.Add(next)) break;
                current = next;
            }
            return current;
        }

        // ── Lua comment stripper ────────────────────────────────────────────
        //
        // Replaces comments with spaces (never deletes characters) so line
        // structure survives — the alias pass is line-based and a stripper that
        // joined lines would invent statements that were never written.
        //
        // Handles: "..." and '...' with backslash escapes, long-bracket strings
        // [[ ]] / [==[ ]==], line comments, and long-bracket block comments. A
        // regex cannot do this correctly: `local msg = "-- not a comment"` and
        // `--[[ awardBadge("ghost") ]]` both matter, and the second one is the
        // reason stripping is done at all — a commented-out call must not add a
        // badge card to someone's panel.
        internal static string StripComments(string src)
        {
            if (string.IsNullOrEmpty(src)) return "";

            var sb = new StringBuilder(src.Length);
            int i = 0;
            int n = src.Length;

            while (i < n)
            {
                char c = src[i];

                // Line / block comment
                if (c == '-' && i + 1 < n && src[i + 1] == '-')
                {
                    int level;
                    int afterOpen = MatchLongBracketOpen(src, i + 2, out level);
                    if (afterOpen >= 0)
                    {
                        sb.Append("  ");
                        i = BlankLongBracket(src, i + 2, afterOpen, level, sb);
                    }
                    else
                    {
                        while (i < n && src[i] != '\n') { sb.Append(' '); i++; }
                    }
                    continue;
                }

                // Long-bracket string — content is preserved (it is a value, not
                // a comment) but it must not be scanned for comment openers.
                if (c == '[')
                {
                    int level;
                    int afterOpen = MatchLongBracketOpen(src, i, out level);
                    if (afterOpen >= 0)
                    {
                        int end = FindLongBracketClose(src, afterOpen, level);
                        if (end < 0) end = n;
                        sb.Append(src, i, Math.Min(end, n) - i);
                        i = end;
                        continue;
                    }
                }

                // Quoted string
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(c);
                    i++;
                    while (i < n)
                    {
                        char s = src[i];
                        sb.Append(s);
                        i++;
                        if (s == '\\' && i < n) { sb.Append(src[i]); i++; continue; }
                        if (s == quote) break;
                        if (s == '\n') break;   // unterminated literal; don't run away
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        // At `start`, matches `[`, then N `=`, then `[`. Returns the index just
        // past the opener and sets `level` to N; returns -1 when it isn't one.
        private static int MatchLongBracketOpen(string src, int start, out int level)
        {
            level = 0;
            if (start >= src.Length || src[start] != '[') return -1;
            int i = start + 1;
            while (i < src.Length && src[i] == '=') { level++; i++; }
            if (i < src.Length && src[i] == '[') return i + 1;
            level = 0;
            return -1;
        }

        private static int FindLongBracketClose(string src, int from, int level)
        {
            string close = "]" + new string('=', level) + "]";
            int idx = src.IndexOf(close, from, StringComparison.Ordinal);
            return idx < 0 ? -1 : idx + close.Length;
        }

        // Blanks a long-bracket COMMENT, keeping newlines so line numbers and
        // the line-based alias pass stay honest.
        private static int BlankLongBracket(string src, int openStart, int afterOpen, int level, StringBuilder sb)
        {
            int end = FindLongBracketClose(src, afterOpen, level);
            if (end < 0) end = src.Length;
            for (int k = openStart; k < end; k++)
                sb.Append(src[k] == '\n' ? '\n' : ' ');
            return end;
        }
    }
}
#endif
