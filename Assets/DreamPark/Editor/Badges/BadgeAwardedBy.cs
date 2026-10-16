#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Text;

namespace DreamPark.Badges
{
    // Builds the `awardedBy` field BadgeUploader posts with each badge, and the
    // earnAt name checks BadgeEarnAtCheck and the panel share.
    //
    // PURE on purpose — no UnityEditor, no AssetDatabase — so the rules below
    // are checkable offline. The Unity side (which prefabs exist, what address
    // each one ships under) is gathered by BadgeUploader.CollectSites and handed
    // in as plain data.
    //
    // ── The server contract this has to satisfy ─────────────────────────────
    //
    //  POST /admin/content/:id/badges/save, field `awardedBy` = JSON array of
    //  {kind, contentId, resourceName, name?, scriptPath?}
    //  (DreamPark-Web routes/admin.content.routes.js, lib/badgeCatalog.js
    //  parseAwardedByField / normalizeAwardedByEntry, docs/contracts/badges.md):
    //
    //   • field ABSENT  → stored attribution and availability left alone.
    //   • `[]` or ""    → an explicit CLEAR, and it VAULTS the badge.
    //   • [rows]        → replace; the badge is live again.
    //   • kind ∈ attraction|prop|player|script; attraction/prop rows without a
    //     resourceName are DROPPED (and if every row drops, that is a clear).
    //   • `slug` is derived server-side from resourceName — never sent.
    //
    //  So: Build never emits a placed row without a resourceName. The
    //  AUTOMATED DATA IS AUTHORITATIVE on every push (Aidan, Oct 16): BuildField
    //  sends `[]` for a badge with no award site and no manual link, so severed
    //  links are cleared and the server marks it not awarded in this version
    //  (it comes back once a site or manual link exists). It sends NOTHING
    //  (omit) when the scan itself failed, or when the only link is a manual
    //  name that doesn't resolve — a broken scan or a typo must never wipe or
    //  vault good data.
    //
    // ── resourceName is the ADDRESS, not the prefab name ────────────────────
    //
    //  Parks store objects[].resourceName = the Addressables address
    //  "{gameId}/Levels/{size}/{fileStem}" (ContentProcessor.ResolveAttractionAddress,
    //  stamped at upload from the FILE NAME — a prefab's serialized
    //  GameArea.resourceName can legitimately be empty until then). The server
    //  joins badges to placed attractions on slug(resourceName), so a bare
    //  "A_Cannonsnout" would never match a park. earnAt (the MANUAL link) is
    //  authored as the prefab name for readability; it is resolved to the
    //  address here through the package's own attraction / prop prefabs.
    public static class BadgeAwardedBy
    {
        public const string KindAttraction = "attraction";
        public const string KindProp = "prop";
        public const string KindPlayer = "player";
        public const string KindScript = "script";

        // Mirrors lib/badgeCatalog.js MAX_AWARDED_BY; the server truncates past it.
        public const int MaxRows = 64;

        /// One placeable award site: a prefab in the package and the address it
        /// ships under for the upload being made.
        public sealed class Site
        {
            public string kind;          // KindAttraction or KindProp
            public string prefabName;    // file stem, e.g. "A_Cannonsnout"
            public string resourceName;  // "{gameId}/Levels/{size}/{prefabName}"
            public string name;          // display name, optional
        }

        public sealed class Row
        {
            public string kind;
            public string contentId;
            public string resourceName;  // attraction/prop only
            public string name;          // optional
            public string scriptPath;    // script rows: where the award call was found (display only)
        }

        // ── earnAt names ─────────────────────────────────────────────────

        /// Trim, drop blanks, dedupe (ordinal, first wins), keep authored order.
        /// Never returns null, so a file written by an older SDK (no field) and
        /// one with `"earnAt": []` read identically.
        public static List<string> NormalizeEarnAt(IEnumerable<string> earnAt)
        {
            var result = new List<string>();
            if (earnAt == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in earnAt)
            {
                string n = (raw ?? "").Trim();
                if (n.Length == 0 || !seen.Add(n)) continue;
                result.Add(n);
            }
            return result;
        }

        /// The prefab name an earnAt entry refers to. Authored entries are bare
        /// prefab names; a full address ("SAL/Levels/Large/A_X") is tolerated and
        /// reduced to its last segment, so either spelling validates and uploads.
        public static string PrefabNameOf(string earnAtName)
        {
            string n = (earnAtName ?? "").Trim();
            int slash = n.LastIndexOf('/');
            return slash >= 0 ? n.Substring(slash + 1) : n;
        }

        /// Parse the panel's comma-separated earnAt text field.
        public static List<string> ParseEarnAtText(string text)
        {
            return NormalizeEarnAt((text ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.None));
        }

        public static string EarnAtText(IEnumerable<string> earnAt)
        {
            return string.Join(", ", NormalizeEarnAt(earnAt).ToArray());
        }

        public enum EarnAtProblem { None, Missing }

        /// Is one earnAt name a manual link to a real prefab? It may name an
        /// attraction OR a prop root of the package (the upload row's kind
        /// follows the prefab). Case-sensitive, like the addresses.
        public static EarnAtProblem Classify(string earnAtName,
                                             ICollection<string> attractionPrefabNames,
                                             ICollection<string> propPrefabNames)
        {
            string n = PrefabNameOf(earnAtName);
            if (n.Length == 0) return EarnAtProblem.None;
            if (attractionPrefabNames != null && attractionPrefabNames.Contains(n)) return EarnAtProblem.None;
            if (propPrefabNames != null && propPrefabNames.Contains(n)) return EarnAtProblem.None;
            return EarnAtProblem.Missing;
        }

        /// A likely intended prefab for a name that didn't match: same name
        /// ignoring case, or the name with the "A_" attraction prefix added.
        public static string SuggestPrefab(string earnAtName, IEnumerable<string> prefabNames)
        {
            string n = PrefabNameOf(earnAtName);
            if (n.Length == 0 || prefabNames == null) return null;
            string prefixed = n.StartsWith("A_", StringComparison.Ordinal) ? null : "A_" + n;
            string ci = null, pre = null;
            foreach (string a in prefabNames)
            {
                if (string.IsNullOrEmpty(a) || a == n) continue;
                if (ci == null && string.Equals(a, n, StringComparison.OrdinalIgnoreCase)) ci = a;
                if (pre == null && prefixed != null && string.Equals(a, prefixed, StringComparison.OrdinalIgnoreCase)) pre = a;
            }
            return ci ?? pre;
        }

        /// Inner levels: attraction prefabs nested inside ANOTHER attraction
        /// prefab (a Sequence wrapper such as A_LionheartBoss holding
        /// Level32_Boss_Lionheart). The app only recognises the WRAPPER — nothing
        /// sent to the phone maps an inner level to it — so a manual link must
        /// name the wrapper. Returns inner path → wrapper paths (sorted).
        ///   prefabDeps:   prefab path → the prefab paths it references directly
        ///                 (AssetDatabase.GetDependencies(path, false), .prefab only)
        ///   attractions:  every attraction root path of the package
        ///   variantBases: attraction path → its variant base chain (excluded:
        ///                 a variant "contains" its base, but isn't a wrapper)
        public static Dictionary<string, List<string>> InnerLevelWrappers(
            IDictionary<string, IEnumerable<string>> prefabDeps,
            ICollection<string> attractions,
            IDictionary<string, ICollection<string>> variantBases = null)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (prefabDeps == null || attractions == null) return result;
            foreach (string wrapper in attractions)
            {
                ICollection<string> bases = null;
                if (variantBases != null) variantBases.TryGetValue(wrapper, out bases);
                var seen = new HashSet<string>(StringComparer.Ordinal) { wrapper };
                var stack = new Stack<string>();
                stack.Push(wrapper);
                while (stack.Count > 0)
                {
                    if (!prefabDeps.TryGetValue(stack.Pop(), out var deps) || deps == null) continue;
                    foreach (string dep in deps)
                    {
                        if (string.IsNullOrEmpty(dep) || !seen.Add(dep)) continue;
                        if (bases != null && bases.Contains(dep)) continue;   // the variant's own base chain
                        if (attractions.Contains(dep))
                        {
                            if (!result.TryGetValue(dep, out var list)) result[dep] = list = new List<string>();
                            if (!list.Contains(wrapper)) list.Add(wrapper);
                        }
                        stack.Push(dep);
                    }
                }
            }
            foreach (var list in result.Values) list.Sort(StringComparer.Ordinal);
            return result;
        }

        // ── awardedBy ────────────────────────────────────────────────────

        /// Uploaded awardedBy = automatic inference ∪ authored earnAt:
        /// the scanner's rows (Attraction/Prop roots whose Lua awards the badge
        /// or that CARRY its id — BadgeAttributionScanner) plus earnAt (manual
        /// links) → 'attraction' / 'prop' rows by the named prefab's kind,
        /// resolved through placedByName. Deduped on (kind, resourceName);
        /// earnAt rows come first. 'player' is added only when no placed row
        /// exists, and a 'script' row only when neither a placed row nor the
        /// Player awards it but the package's Lua still does (luaScriptPath).
        ///
        /// STALE LINKS: the server REPLACES awardedBy with whatever is sent
        /// (set merge:true replaces the top-level array). So every badge that
        /// is awarded anywhere sends a non-empty, authoritative list — a badge
        /// whose attraction links were removed but that the Player still
        /// awards sends [player], which overwrites the old links and keeps it
        /// live. A badge with no award site at all gets `[]` from BuildField. An earnAt
        /// name with no attraction/prop prefab is skipped here — a row that names no
        /// shipped address can never join a park — and BadgeEarnAtCheck has
        /// already warned about it. Never null; may be empty.
        public static List<Row> Build(string contentId,
                                      IEnumerable<string> earnAt,
                                      IDictionary<string, Site> placedByName,
                                      IEnumerable<Site> scannedSites,
                                      bool awardedByPlayer,
                                      List<string> unresolved = null,
                                      string luaScriptPath = null)
        {
            var rows = new List<Row>();
            contentId = (contentId ?? "").Trim();
            if (contentId.Length == 0) return rows;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string raw in NormalizeEarnAt(earnAt))
            {
                Site site;
                if (placedByName == null
                    || !placedByName.TryGetValue(PrefabNameOf(raw), out site)
                    || site == null || string.IsNullOrEmpty(site.resourceName))
                {
                    if (unresolved != null) unresolved.Add(raw);
                    continue;
                }
                AddPlaced(rows, seen, contentId, site.kind == KindProp ? KindProp : KindAttraction, site);
            }

            if (scannedSites != null)
            {
                foreach (Site site in scannedSites)
                {
                    if (site == null || string.IsNullOrEmpty(site.resourceName)) continue;
                    string kind = site.kind == KindProp ? KindProp : KindAttraction;
                    AddPlaced(rows, seen, contentId, kind, site);
                }
            }

            // 'player' only when nothing PLACED awards it. Titles award from one
            // Player-side dispatcher (SAL sal-badges.lua, ArcadeLand
            // arcade_badges.lua), so "the Player's Lua calls awardBadge" is true
            // of nearly every badge; once an attraction/prop is known, that row
            // is where it's earned, and a player row would flip the server's
            // awardedByPlayer ("reachable anywhere") on for an attraction badge.
            if (awardedByPlayer && rows.Count == 0)
                rows.Add(new Row { kind = KindPlayer, contentId = contentId });

            // Awarded by package Lua the scan couldn't trace to any root (the
            // contract's honest 'script' answer): still earnable, so it stays
            // live and any old attraction links are replaced.
            if (rows.Count == 0 && !string.IsNullOrEmpty(luaScriptPath))
                rows.Add(new Row { kind = KindScript, contentId = contentId, scriptPath = luaScriptPath.Trim() });

            if (rows.Count > MaxRows) rows.RemoveRange(MaxRows, rows.Count - MaxRows);
            return rows;
        }

        private static void AddPlaced(List<Row> rows, HashSet<string> seen, string contentId,
                                      string kind, Site site)
        {
            if (!seen.Add(kind + "|" + site.resourceName)) return;
            rows.Add(new Row
            {
                kind = kind,
                contentId = contentId,
                resourceName = site.resourceName,
                name = string.IsNullOrEmpty(site.name) ? null : site.name.Trim(),
            });
        }

        /// The rows as JSON, or NULL for no rows. Never returns "[]" or "" —
        /// the deliberate clear is BuildField's decision, not this one's.
        public static string ToJson(IList<Row> rows)
        {
            if (rows == null || rows.Count == 0) return null;

            var sb = new StringBuilder("[");
            int written = 0;
            foreach (Row r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.kind) || string.IsNullOrEmpty(r.contentId)) continue;
                bool placed = r.kind == KindAttraction || r.kind == KindProp;
                if (placed && string.IsNullOrEmpty(r.resourceName)) continue;

                if (written++ > 0) sb.Append(',');
                sb.Append("{\"kind\":").Append(Quote(r.kind));
                sb.Append(",\"contentId\":").Append(Quote(r.contentId));
                if (placed) sb.Append(",\"resourceName\":").Append(Quote(r.resourceName));
                if (!string.IsNullOrEmpty(r.name)) sb.Append(",\"name\":").Append(Quote(r.name));
                if (!string.IsNullOrEmpty(r.scriptPath)) sb.Append(",\"scriptPath\":").Append(Quote(r.scriptPath));
                sb.Append('}');
            }
            if (written == 0) return null;
            return sb.Append(']').ToString();
        }

        public const string ClearField = "[]";

        /// The `awardedBy` field for one badge on a push, or NULL = omit it.
        ///   rows                              → the JSON list (replaces stored links)
        ///   no rows, scan OK, nothing pending → "[]": links cleared, badge marked
        ///                                       not awarded in this version
        ///   scan failed / errored             → null: never wipe or vault on a broken scan
        ///   no rows only because a manual link
        ///   didn't resolve (typo, rename)     → null: badge-earn-at warns; don't vault on it
        public static string BuildField(string contentId, IEnumerable<string> earnAt,
                                        IDictionary<string, Site> placedByName,
                                        IEnumerable<Site> scannedSites, bool awardedByPlayer,
                                        bool scanSucceeded,
                                        List<string> unresolved = null, string luaScriptPath = null)
        {
            if (!scanSucceeded || string.IsNullOrEmpty((contentId ?? "").Trim())) return null;
            var pending = unresolved ?? new List<string>();
            int before = pending.Count;
            string json = BuildJson(contentId, earnAt, placedByName, scannedSites, awardedByPlayer, pending, luaScriptPath);
            if (json != null) return json;
            if (pending.Count > before) return null;
            return ClearField;
        }

        public static string BuildJson(string contentId, IEnumerable<string> earnAt,
                                       IDictionary<string, Site> placedByName,
                                       IEnumerable<Site> scannedSites, bool awardedByPlayer,
                                       List<string> unresolved = null, string luaScriptPath = null)
        {
            return ToJson(Build(contentId, earnAt, placedByName, scannedSites, awardedByPlayer, unresolved, luaScriptPath));
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
#endif
