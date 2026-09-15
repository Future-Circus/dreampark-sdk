#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DreamPark.PreUploadChecks;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Badges
{
    // For each badge id BadgeLuaScanner already knows about, works out WHICH
    // Attraction/Prop/Player content roots actually award it — so the panel can
    // show "Awarded by: X, Y" and the uploader can send that attribution to the
    // backend (badge doc + the attraction/prop's own metadata).
    //
    // ── Why this is a second pass and not a field on BadgeLuaScanner.Discovery ──
    //
    // BadgeLuaScanner answers "does this content package define this badge id
    // anywhere", scanning script text package-wide and deduplicating by id
    // (first writer wins — see AddDiscovery). That is the right shape for the
    // panel's badge list, but it is the WRONG shape for attribution: a Lua
    // script can be shared by more than one prefab (a common collectible prop
    // reused across several attractions, say), and an @var can be overridden to
    // a DIFFERENT id on each instance. A single scriptPath cannot answer "which
    // roots award this", and a single discovery cannot answer "one script, two
    // ids" when two instances override the same @var differently.
    //
    // So this scanner walks each ROOT's own hierarchy instead of the package's
    // script list, resolving the exact same three shapes BadgeLuaScanner does
    // (literal / @var default / instance override) against each root's own
    // ILuaInjectable components. It shares BadgeLuaScanner's regexes and
    // helpers rather than re-implementing them — see the `internal` marks on
    // those members.
    //
    // ── Deliberate limits ─────────────────────────────────────────────────
    //
    //  • Same "prefabs only" scope as BadgeLuaScanner shape 3: a LuaBehaviour
    //    living only in a scene is invisible here too.
    //  • An id this resolves to is added to a root's set even when the same id
    //    is ALSO discoverable as a plain literal elsewhere — attribution is
    //    additive per root, not exclusive.
    //  • Costs one MonoBehaviour walk per content root (already loaded by other
    //    pre-upload checks) plus a regex pass per Lua-carrying component on
    //    that root. Same order of magnitude BadgeLuaScanner already pays on
    //    every RefreshBadges(), so it is safe to run in the advisory scan.
    public static class BadgeAttributionScanner
    {
        public sealed class Result
        {
            // badgeId -> the Attraction/Prop roots that award it, in Scan() order.
            public Dictionary<string, List<ContentRootInfo>> awardedByRoot =
                new Dictionary<string, List<ContentRootInfo>>(System.StringComparer.Ordinal);

            // badgeId -> true if some Player root's own Lua awards it. Kept
            // separate from awardedByRoot rather than folding the Player root
            // into the same list: "awarded by the player rig" is a different
            // kind of fact from "awarded by this specific attraction" — the
            // former is global to the content package, the latter is scoped to
            // one placeable asset — and callers (the panel label, the upload
            // payload) want to ask these two questions separately.
            public HashSet<string> awardedByPlayer = new HashSet<string>(System.StringComparer.Ordinal);

            public bool IsAwardedAnywhere(string badgeId)
            {
                if (string.IsNullOrEmpty(badgeId)) return false;
                return awardedByPlayer.Contains(badgeId)
                    || (awardedByRoot.TryGetValue(badgeId, out var list) && list.Count > 0);
            }
        }

        public static Result Scan(string contentId, IReadOnlyList<ContentRootInfo> roots)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(contentId) || roots == null) return result;

            foreach (ContentRootInfo root in roots)
            {
                if (root == null || string.IsNullOrEmpty(root.assetPath)) continue;

                GameObject prefab;
                try { prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root.assetPath); }
                catch { continue; }
                if (prefab == null) continue;

                foreach (string id in AwardedIdsOnRoot(prefab))
                {
                    if (root.kind == ContentRootKindPublic.Player)
                    {
                        result.awardedByPlayer.Add(id);
                        continue;
                    }

                    if (!result.awardedByRoot.TryGetValue(id, out var list))
                    {
                        list = new List<ContentRootInfo>();
                        result.awardedByRoot[id] = list;
                    }
                    list.Add(root);
                }
            }

            return result;
        }

        // Every badge id this ONE root's own Lua would actually award, walking
        // its hierarchy exactly like BadgeLuaScanner's ScanPrefabsForInspectorValues
        // does (GetComponentsInChildren picks up nested prefab instances too).
        private static IEnumerable<string> AwardedIdsOnRoot(GameObject root)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var injectable = mb as ILuaInjectable;
                if (injectable == null) continue;

                var script = injectable.luaScript;
                if (script == null) continue;

                string source;
                try { source = script.text; }
                catch { continue; }
                if (string.IsNullOrEmpty(source)) continue;

                var declaredStringVars = BadgeLuaScanner.ParseStringVarDefaults(source);
                string stripped = BadgeLuaScanner.StripComments(source);
                var aliases = BadgeLuaScanner.BuildAliasMap(stripped);

                foreach (Match m in BadgeLuaScanner.CallRegex.Matches(stripped))
                {
                    string arg = m.Groups[1].Value.Trim();
                    if (arg.Length == 0) continue;

                    var lit = BadgeLuaScanner.StringLiteralRegex.Match(arg);
                    if (lit.Success)
                    {
                        string id = (lit.Groups[1].Success ? lit.Groups[1].Value : lit.Groups[2].Value).Trim();
                        if (id.Length > 0) ids.Add(id);
                        continue;
                    }

                    if (!BadgeLuaScanner.IdentifierRegex.IsMatch(arg)) continue;

                    string varName = BadgeLuaScanner.ResolveAlias(arg, aliases);

                    // Prefer THIS instance's own serialized override — it is
                    // what actually runs on this placement — and only fall back
                    // to the @var default when this instance leaves it unset.
                    // Matches Lua's own semantics for an unset @var.
                    string instanceValue = InstanceStringValue(injectable, varName);
                    if (!string.IsNullOrEmpty(instanceValue))
                    {
                        ids.Add(instanceValue);
                        continue;
                    }

                    if (declaredStringVars.TryGetValue(varName, out string defaultValue)
                        && !string.IsNullOrEmpty(defaultValue))
                    {
                        ids.Add(defaultValue);
                    }
                }
            }

            return ids;
        }

        private static string InstanceStringValue(ILuaInjectable injectable, string varName)
        {
            var strings = injectable.stringInjections;
            if (strings == null) return null;

            foreach (var inj in strings)
            {
                if (inj == null || inj.name != varName) continue;
                string v = (inj.value ?? "").Trim();
                return v.Length > 0 ? v : null;
            }
            return null;
        }
    }
}
#endif
