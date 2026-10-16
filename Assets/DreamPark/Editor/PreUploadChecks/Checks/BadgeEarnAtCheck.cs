// ─────────────────────────────────────────────────────────────────────
//  BadgeEarnAtCheck.cs — a badge's earnAt (manual link) names an attraction
//  or prop prefab this package doesn't have.
//
//  WHY THIS EXISTS
//
//  `.badges.json` entries carry an optional, hand-authored `earnAt` list: the
//  attraction (or prop) prefab names (every size / variant) where a player can
//  earn the badge — the manual link, added on top of what
//  BadgeAttributionScanner infers automatically. Today's Challenge uses it on the phone to float the badge over that
//  attraction in the diorama, or to offer "Add <Attraction> to your park?".
//  BadgeUploader resolves each name to the address the upload stamps
//  ("{gameId}/Levels/{size}/{name}", "{gameId}/Props/{category}/{name}")
//  through the package's own attraction / prop prefabs — so a typo or a
//  renamed / deleted prefab resolves to nothing and that site is simply not
//  uploaded. Without this check that loss would
//  be silent: the badge would quietly stop pointing at its attraction.
//
//  WHY WARNING AND NOT BLOCKING
//
//  Same policy as BadgeAwardCheck: the badge itself still uploads and is still
//  earnable; only the "where" hint for that one name is missing. Worth a look
//  before shipping, never worth a modal that can't be argued with.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Badges;
using UnityEditor;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class BadgeEarnAtCheck : IPreUploadCheck
    {
        public const string CheckId = "badge-earn-at";

        // subKey = "<mode>:<badgeId>|<earnAt name>". Per name, so ignoring one
        // stale name never pre-ignores a different one added later.
        // PreUploadCheckRunner.BuildBadgeAwardMap reads the badge id back out
        // (everything between the first ':' and the first '|').
        public const string ModeMissing = "earnat-missing";
        // An inner level nested in a Sequence wrapper attraction: the app only
        // recognises the wrapper, so the link must name it.
        public const string ModeInnerLevel = "earnat-inner";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Badge earnAt links exist"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // Reads .badges.json and ContentRootInfo names; prefab dependency lookups
        // (no loads, except a variant's base chain) only when a manual link
        // names an attraction, to spot inner levels.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "Each name in a badge's earnAt (manual link) must be an attraction or prop prefab "
                     + "in this package. The uploader turns those names into the addresses the app uses to "
                     + "show where a badge can be earned, so a name with no prefab behind it is dropped "
                     + "from the upload.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.contentId))
                return CheckResult.Skipped(CheckId, "no content selected");

            List<BadgeStore.Entry> badges;
            try { badges = BadgeStore.Load(ctx.contentId); }
            catch (Exception e) { return CheckResult.Errored(CheckId, "could not read badges: " + e.Message); }

            if (badges == null || !badges.Any(b => b != null && b.earnAt != null && b.earnAt.Count > 0))
                return CheckResult.Clean(CheckId);

            var attractions = new HashSet<string>(StringComparer.Ordinal);
            var props = new HashSet<string>(StringComparer.Ordinal);
            if (ctx.roots != null)
            {
                foreach (var r in ctx.roots)
                {
                    if (r == null || string.IsNullOrEmpty(r.name)) continue;
                    if (r.kind == ContentRootKindPublic.Attraction) attractions.Add(r.name);
                    else if (r.kind == ContentRootKindPublic.Prop) props.Add(r.name);
                }
            }

            var findings = new List<Finding>();
            string assetPath = BadgeStore.PathFor(ctx.contentId);
            Dictionary<string, List<string>> wrappersByInnerName = null;   // built lazily

            foreach (var badge in badges)
            {
                if (badge == null || string.IsNullOrEmpty(badge.badgeId)) continue;
                foreach (string name in BadgeAwardedBy.NormalizeEarnAt(badge.earnAt))
                {
                    var problem = BadgeAwardedBy.Classify(name, attractions, props);
                    if (problem != BadgeAwardedBy.EarnAtProblem.None)
                    {
                        findings.Add(BuildFinding(ctx.contentId, assetPath, badge.badgeId, name,
                                                  attractions.Concat(props).ToList()));
                        continue;
                    }
                    string prefab = BadgeAwardedBy.PrefabNameOf(name);
                    if (!attractions.Contains(prefab)) continue;
                    if (wrappersByInnerName == null) wrappersByInnerName = InnerLevels(ctx.roots);
                    if (wrappersByInnerName.TryGetValue(prefab, out var wrappers) && wrappers.Count > 0)
                        findings.Add(BuildInnerFinding(ctx.contentId, assetPath, badge, name, wrappers));
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        private static Finding BuildFinding(string contentId, string assetPath, string badgeId, string name,
                                            ICollection<string> prefabNames)
        {
            string detail = "Badge '" + badgeId + "' lists '" + name + "' in earnAt, but there is no attraction "
                  + "or prop prefab named '" + BadgeAwardedBy.PrefabNameOf(name) + "' under Assets/Content/"
                  + contentId + ". It may be misspelled, renamed or deleted; this name will not be uploaded, "
                  + "so the app can't point players at it.";

            string suggestion = BadgeAwardedBy.SuggestPrefab(name, prefabNames);
            if (!string.IsNullOrEmpty(suggestion)) detail += "\n\nDid you mean '" + suggestion + "'?";

            var finding = new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Warning,
                assetGuid = "",           // .badges.json is dot-prefixed — no GUID
                assetPath = assetPath,
                subKey = ModeMissing + ":" + badgeId + "|" + name,
                title = "Badge '" + badgeId + "' earnAt '" + name + "' isn't an attraction or prop in this package",
                detail = detail,
            };

            finding.fixes.Add(new FixAction("Remove from earnAt", () => RemoveName(contentId, badgeId, name))
            {
                canBulk = false,
                affectedPaths = new[] { assetPath },
                confirmTitle = "Remove earnAt name?",
                confirmMessage = "Remove '" + name + "' from badge '" + badgeId + "' earnAt in " + assetPath + "?",
                tooltip = "Edits only this badge's local earnAt list.",
            });
            if (!string.IsNullOrEmpty(suggestion))
            {
                string fixedName = suggestion;
                finding.fixes.Add(new FixAction("Use '" + fixedName + "'", () => ReplaceName(contentId, badgeId, name, fixedName))
                {
                    canBulk = false,
                    affectedPaths = new[] { assetPath },
                    tooltip = "Replaces '" + name + "' with '" + fixedName + "' in this badge's earnAt.",
                });
            }
            return finding;
        }

        // Attraction file stem → the wrapper attraction stems it is nested in.
        private static Dictionary<string, List<string>> InnerLevels(IReadOnlyList<ContentRootInfo> roots)
        {
            var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (roots == null) return byName;
            var attractionPaths = new HashSet<string>(StringComparer.Ordinal);
            var nameOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in roots)
                if (r != null && r.kind == ContentRootKindPublic.Attraction && !string.IsNullOrEmpty(r.assetPath))
                {
                    attractionPaths.Add(r.assetPath);
                    nameOf[r.assetPath] = r.name;
                }

            var deps = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
            var bases = new Dictionary<string, ICollection<string>>(StringComparer.Ordinal);
            var queue = new Queue<string>(attractionPaths);
            while (queue.Count > 0)
            {
                string p = queue.Dequeue();
                if (deps.ContainsKey(p)) continue;
                string[] direct;
                try { direct = AssetDatabase.GetDependencies(p, false); }
                catch { direct = new string[0]; }
                var prefabs = direct.Where(d => d != p && d.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToList();
                deps[p] = prefabs;
                foreach (var d in prefabs) if (!deps.ContainsKey(d)) queue.Enqueue(d);
            }
            foreach (string p in attractionPaths)
            {
                var chain = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var go = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(p);
                    while (go != null && PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.Variant)
                    {
                        var baseGo = PrefabUtility.GetCorrespondingObjectFromSource(go);
                        string bp = baseGo != null ? AssetDatabase.GetAssetPath(baseGo) : null;
                        if (string.IsNullOrEmpty(bp) || !chain.Add(bp)) break;
                        go = baseGo;
                    }
                }
                catch { }
                if (chain.Count > 0) bases[p] = chain;
            }

            foreach (var pair in BadgeAwardedBy.InnerLevelWrappers(deps, attractionPaths, bases))
            {
                if (!nameOf.TryGetValue(pair.Key, out string inner)) continue;
                byName[inner] = pair.Value.Select(w => nameOf.TryGetValue(w, out var n) ? n : w).ToList();
            }
            return byName;
        }

        private static Finding BuildInnerFinding(string contentId, string assetPath, BadgeStore.Entry badge,
                                                 string name, List<string> wrappers)
        {
            string badgeId = badge.badgeId;
            var linked = BadgeAwardedBy.NormalizeEarnAt(badge.earnAt).Select(BadgeAwardedBy.PrefabNameOf).ToList();
            string wrapperList = string.Join(", ", wrappers.Select(w => "'" + w + "'").ToArray());
            var finding = new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Warning,
                assetGuid = "",
                assetPath = assetPath,
                subKey = ModeInnerLevel + ":" + badgeId + "|" + name,
                title = "Badge '" + badgeId + "' earnAt '" + name + "' is a level nested inside " + wrapperList,
                detail = "'" + BadgeAwardedBy.PrefabNameOf(name) + "' is nested inside the attraction " + wrapperList
                       + ". The app recognises a nested level only by its WRAPPER attraction — nothing sent to "
                       + "the phone maps the inner level to it — so this link would never match a park. Link the "
                       + "wrapper instead (if this prefab is ALSO placed on its own, keep both).",
            };
            string first = wrappers[0];
            if (linked.Contains(first))
            {
                finding.fixes.Add(new FixAction("Remove from earnAt", () => ReplaceName(contentId, badgeId, name, null))
                {
                    canBulk = false,
                    affectedPaths = new[] { assetPath },
                    tooltip = "'" + first + "' is already linked; drops the inner level.",
                });
            }
            else
            {
                finding.fixes.Add(new FixAction("Use '" + first + "'", () => ReplaceName(contentId, badgeId, name, first))
                {
                    canBulk = false,
                    affectedPaths = new[] { assetPath },
                    tooltip = "Replaces '" + name + "' with its wrapper '" + first + "' in this badge's earnAt.",
                });
            }
            return finding;
        }

        private static bool RemoveName(string contentId, string badgeId, string name)
        {
            return ReplaceName(contentId, badgeId, name, null);
        }

        private static bool ReplaceName(string contentId, string badgeId, string name, string replacement)
        {
            var entries = BadgeStore.Load(contentId);
            var entry = entries.FirstOrDefault(e => e != null && string.Equals(e.badgeId, badgeId, StringComparison.Ordinal));
            if (entry == null || entry.earnAt == null) return false;
            int i = entry.earnAt.IndexOf(name);
            if (i < 0) return false;
            if (string.IsNullOrEmpty(replacement)) entry.earnAt.RemoveAt(i);
            else entry.earnAt[i] = replacement;
            BadgeStore.Save(contentId, entries);
            PreUploadCheckRunner.InvalidateCache(contentId);
            return true;
        }
    }
}
#endif
