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
    //
    // ── Carried ids (Oct 15 2026) ─────────────────────────────────────────
    //
    // Most titles award from ONE Player-side dispatcher (SAL sal-badges.lua,
    // ArcadeLand arcade_badges.lua): every awardBadge call is a literal on the
    // Player, so the call site alone says "player" for everything. The place a
    // badge is earned shows up instead as a component INSIDE an attraction that
    // CARRIES the id — SAL's E_BadgeReward medal (`@var badgeId`, set per
    // instance) nested in A_KingJubbo, L_CANNON, P_SunShrine… — which hands it
    // to the dispatcher at runtime. So a root also "awards" a KNOWN badge id
    // (one the package's Lua scan / badge drafts define) when anything in its
    // hierarchy carries that exact id:
    //   • a Lua string injection value (instance override or nested-prefab /
    //     variant value — GetComponentsInChildren sees the merged result);
    //   • an attached script's `@var` string default, when the instance
    //     leaves it unset;
    //   • a serialized C# string field whose name contains "badge".
    // Exact match against known ids only, so a free-form string can't invent a
    // badge. The panel shows each root's "via" so the inference is auditable.
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

            // badgeId -> root assetPath -> how it was found ("awardBadge in x.lua
            // on Child/Path", "badgeId on Medal (badge-reward.lua)"). Display only.
            public Dictionary<string, Dictionary<string, string>> via =
                new Dictionary<string, Dictionary<string, string>>(System.StringComparer.Ordinal);

            public bool IsAwardedAnywhere(string badgeId)
            {
                if (string.IsNullOrEmpty(badgeId)) return false;
                return awardedByPlayer.Contains(badgeId)
                    || (awardedByRoot.TryGetValue(badgeId, out var list) && list.Count > 0);
            }

            public string Via(string badgeId, string assetPath)
            {
                if (badgeId != null && assetPath != null
                    && via.TryGetValue(badgeId, out var byPath)
                    && byPath.TryGetValue(assetPath, out var v)) return v;
                return "";
            }
        }

        /// knownIds: the badge ids the package defines (Lua scan + drafts). Only
        /// these can be inferred from a carried string. Null = compute them.
        public static Result Scan(string contentId, IReadOnlyList<ContentRootInfo> roots,
                                  IEnumerable<string> knownIds = null)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(contentId) || roots == null) return result;

            var known = new HashSet<string>(knownIds ?? KnownIds(contentId), System.StringComparer.Ordinal);
            known.Remove("");
            known.Remove(null);

            foreach (ContentRootInfo root in roots)
            {
                if (root == null || string.IsNullOrEmpty(root.assetPath)) continue;

                GameObject prefab;
                try { prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root.assetPath); }
                catch { continue; }
                if (prefab == null) continue;

                foreach (var pair in AwardedIdsOnRoot(prefab, known))
                {
                    string id = pair.Key;
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

                    if (!result.via.TryGetValue(id, out var byPath))
                    {
                        byPath = new Dictionary<string, string>(System.StringComparer.Ordinal);
                        result.via[id] = byPath;
                    }
                    byPath[root.assetPath] = pair.Value;
                }
            }

            return result;
        }

        private static IEnumerable<string> KnownIds(string contentId)
        {
            var ids = new List<string>();
            try
            {
                foreach (var d in BadgeLuaScanner.Scan(contentId).discoveries)
                    if (d != null && !string.IsNullOrEmpty(d.badgeId)) ids.Add(d.badgeId);
                foreach (var e in BadgeStore.Load(contentId))
                    if (e != null && !string.IsNullOrEmpty(e.badgeId)) ids.Add(e.badgeId.Trim());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Badges] known-id scan failed: " + e.Message);
            }
            return ids;
        }

        // Every badge id this ONE root would award (or carries for a dispatcher
        // to award), walking its hierarchy exactly like BadgeLuaScanner's
        // ScanPrefabsForInspectorValues does (GetComponentsInChildren picks up
        // nested prefab instances and variant overrides too). Value = "via".
        private static Dictionary<string, string> AwardedIdsOnRoot(GameObject root, HashSet<string> known)
        {
            var ids = new Dictionary<string, string>(System.StringComparer.Ordinal);
            System.Action<string, string> add = (id, how) =>
            {
                if (!string.IsNullOrEmpty(id) && !ids.ContainsKey(id)) ids[id] = how;
            };

            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string where = PathFrom(root.transform, mb.transform);

                // C# components that carry a badge id in a "badge…" string field.
                foreach (var field in BadgeStringFields(mb.GetType()))
                {
                    string v = null;
                    try { v = ((field.GetValue(mb) as string) ?? "").Trim(); } catch { }
                    if (!string.IsNullOrEmpty(v) && known.Contains(v))
                        add(v, field.Name + " on " + where + " (" + mb.GetType().Name + ")");
                }

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
                string callVia = "awardBadge in " + script.name + " on " + where;

                foreach (Match m in BadgeLuaScanner.CallRegex.Matches(stripped))
                {
                    string arg = m.Groups[1].Value.Trim();
                    if (arg.Length == 0) continue;

                    var lit = BadgeLuaScanner.StringLiteralRegex.Match(arg);
                    if (lit.Success)
                    {
                        string id = (lit.Groups[1].Success ? lit.Groups[1].Value : lit.Groups[2].Value).Trim();
                        if (id.Length > 0) add(id, callVia);
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
                        add(instanceValue, callVia);
                        continue;
                    }

                    if (declaredStringVars.TryGetValue(varName, out string defaultValue)
                        && !string.IsNullOrEmpty(defaultValue))
                    {
                        add(defaultValue, callVia);
                    }
                }

                // Carried ids: any string var on this instance (override, else
                // @var default) that IS a known badge id.
                var seenVars = new HashSet<string>(System.StringComparer.Ordinal);
                if (injectable.stringInjections != null)
                {
                    foreach (var inj in injectable.stringInjections)
                    {
                        if (inj == null || string.IsNullOrEmpty(inj.name)) continue;
                        string v = (inj.value ?? "").Trim();
                        if (v.Length == 0) continue;     // unset → the @var default applies below
                        seenVars.Add(inj.name);
                        if (known.Contains(v))
                            add(v, inj.name + " on " + where + " (" + script.name + ")");
                    }
                }
                foreach (var pair in declaredStringVars)
                {
                    if (seenVars.Contains(pair.Key)) continue;
                    string v = (pair.Value ?? "").Trim();
                    if (v.Length > 0 && known.Contains(v))
                        add(v, "@var " + pair.Key + " default on " + where + " (" + script.name + ")");
                }
            }

            return ids;
        }

        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo[]> badgeFieldCache =
            new Dictionary<System.Type, System.Reflection.FieldInfo[]>();

        // Serialized string fields named like "badge…" (badgeId, rewardBadge…).
        private static System.Reflection.FieldInfo[] BadgeStringFields(System.Type type)
        {
            if (type == null) return new System.Reflection.FieldInfo[0];
            if (badgeFieldCache.TryGetValue(type, out var cached)) return cached;
            var list = new List<System.Reflection.FieldInfo>();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType != typeof(string)) continue;
                    if (f.Name.IndexOf("badge", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool serialized = f.IsPublic
                        ? !f.IsDefined(typeof(System.NonSerializedAttribute), false)
                        : f.IsDefined(typeof(SerializeField), false);
                    if (serialized) list.Add(f);
                }
            }
            cached = list.ToArray();
            badgeFieldCache[type] = cached;
            return cached;
        }

        private static string PathFrom(Transform root, Transform t)
        {
            if (t == null || t == root) return root != null ? root.name : "";
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
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
