// ─────────────────────────────────────────────────────────────────────
//  BadgeAwardCheck.cs — a badge defined for this content package that no
//  script in it (yet) actually awards.
//
//  WHY THIS EXISTS
//
//  The Content Uploader's Badges section (ContentUploaderPanel.DrawBadgesGroup)
//  lets a developer define a badge two ways: BadgeLuaScanner finds one
//  automatically from dp.profile.awardBadge(...) in their Lua, or they type one
//  in by hand with "Add Badge". A hand-typed badge is a completely normal thing
//  to have mid-development — defining the badge before wiring up the attraction
//  that awards it is a legitimate order to work in, and DrawBadgesGroup's own
//  comment says exactly that. But it is also the state a badge is in if the
//  award call was deleted, renamed, or never written at all, and nothing
//  distinguished those two cases before this check — a badge sitting in
//  "Added by hand" forever looks identical to one three minutes from being
//  wired up.
//
//  This check also catches a narrower, sneakier case: a badge IS referenced by
//  a literal or @var default somewhere in the package's Lua, but
//  BadgeAttributionScanner can't find that Lua attached to ANY Attraction,
//  Prop, or Player content root. BadgeLuaScanner's own header names this blind
//  spot — Lua wired onto a LuaBehaviour that lives only in a scene, not on a
//  content-root prefab, reads as "found" to the text scan but never ships,
//  because scenes under Assets/Content are an author-time test bed, not
//  packaged content.
//
//  WHY WARNING AND NOT BLOCKING
//
//  Same policy as every other check in this suite: "A check that cannot tell
//  must not block." A hand-typed badge with no award call yet is not a bug —
//  it might just be next on the list — and the "wired to a script, but not to
//  any content root" case rests on the prefabs-only scope BadgeLuaScanner
//  already documents as a known limit, not a certainty. Both are worth a
//  developer's attention before they upload; neither is worth a modal that
//  can't be argued with.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Badges;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class BadgeAwardCheck : IPreUploadCheck
    {
        public const string CheckId = "badge-award";

        // Separate modes so ignoring one badge's "not wired at all" finding can
        // never also suppress a different badge's "wired, but not on a root"
        // finding — the same lesson ResourceNameAddressCheck cites from
        // DuplicateNamesCheck: two different failure modes must never share a
        // subKey, or ignoring the harmless one silences the real one.
        private const string ModeUnwired = "unwired";
        private const string ModeUnplaced = "unplaced";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Badges awarded somewhere"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // Same cost shape as ResourceNameAddressCheck: one prefab load per
        // content root (already cached by AssetDatabase after the other checks
        // touch the same roots) plus a regex pass per Lua-carrying component.
        // No dependency walk, no scene load — safe on every panel open.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "A badge the panel knows about but no script in this package currently awards — "
                     + "either it was hand-typed and isn't wired up yet, or it's referenced from Lua that "
                     + "isn't attached to any Attraction, Prop, or Player prefab (a script left only in a "
                     + "scene never ships). Either way, this badge won't actually be earnable as the "
                     + "package stands right now.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.contentId))
                return CheckResult.Skipped(CheckId, "no content selected");

            List<BadgeStore.Entry> badges;
            BadgeLuaScanner.Result scan;
            try
            {
                scan = BadgeLuaScanner.Scan(ctx.contentId);
                badges = BadgeStore.Merge(BadgeStore.Load(ctx.contentId), scan.discoveries);
            }
            catch (System.Exception e)
            {
                return CheckResult.Errored(CheckId, "badge scan failed: " + e.Message);
            }

            if (badges.Count == 0) return CheckResult.Clean(CheckId);

            var attribution = BadgeAttributionScanner.Scan(ctx.contentId, ctx.roots);

            var findings = new List<Finding>();
            string assetPath = BadgeStore.PathFor(ctx.contentId);

            foreach (var badge in badges)
            {
                if (string.IsNullOrEmpty(badge.badgeId)) continue;   // a blank card the dev hasn't named yet

                if (!badge.IdLocked)
                {
                    var finding = new Finding
                    {
                        checkId = CheckId,
                        severity = CheckSeverity.Warning,
                        assetGuid = "",           // .badges.json is dot-prefixed — no GUID is ever minted for it
                        assetPath = assetPath,
                        subKey = ModeUnwired + ":" + badge.badgeId,
                        title = "Badge '" + badge.badgeId + "' isn't awarded by any script yet",
                        detail = "This badge was added by hand and no dp.profile.awardBadge(\"" + badge.badgeId
                               + "\") (or matching @var) was found anywhere in this content package's Lua. "
                               + "Normal mid-development — just confirm it's still meant to be wired up "
                               + "before you ship.",
                    };

                    string badgeId = badge.badgeId;
                    string contentId = ctx.contentId;
                    finding.fixes.Add(FixAction.Navigate("Copy awardBadge call", () =>
                    {
                        EditorGUIUtility.systemCopyBuffer =
                            "dp.profile.awardBadge(\"" + EscapeLuaString(badgeId) + "\")";
                    }));
                    finding.fixes.Add(new FixAction("Remove badge draft", () =>
                    {
                        return RemoveManualDraft(contentId, badgeId);
                    })
                    {
                        canBulk = false,
                        affectedPaths = new[] { assetPath },
                        confirmTitle = "Remove badge draft?",
                        confirmMessage = "Remove the local draft for badge '" + badgeId + "'?\n\n"
                                       + "This does not delete a badge that was already uploaded to the "
                                       + "developer portal.",
                        tooltip = "Removes only this hand-authored local badge card. Uploaded badges are unchanged.",
                    });
                    findings.Add(finding);
                    continue;
                }

                if (!attribution.IsAwardedAnywhere(badge.badgeId))
                {
                    var finding = new Finding
                    {
                        checkId = CheckId,
                        severity = CheckSeverity.Warning,
                        assetGuid = "",
                        assetPath = assetPath,
                        subKey = ModeUnplaced + ":" + badge.badgeId,
                        title = "Badge '" + badge.badgeId + "' isn't attached to any Attraction, Prop, or Player",
                        detail = "This badge's award call was found in your Lua (" + badge.discoveredIn + "), "
                               + "but that script isn't attached to any Attraction, Prop, or Player prefab in "
                               + "this package — only prefabs ship, so a LuaBehaviour left in a scene, or on a "
                               + "prefab that isn't one of those three roots, never reaches players.",
                    };

                    // Keep the scanner's structured paths for actions. discoveredIn is
                    // deliberately human-readable and should never be parsed back into
                    // identity.
                    var discovery = scan.discoveries.FirstOrDefault(d =>
                        d != null && string.Equals(d.badgeId, badge.badgeId, StringComparison.Ordinal));
                    string relevantPath = discovery != null && !string.IsNullOrEmpty(discovery.prefabPath)
                        ? discovery.prefabPath
                        : discovery != null ? discovery.scriptPath : null;

                    if (!string.IsNullOrEmpty(relevantPath))
                    {
                        string path = relevantPath;
                        finding.fixes.Add(FixAction.Navigate(
                            path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                                ? "Open referenced prefab"
                                : "Open awarding script",
                            () => OpenAsset(path)));
                    }

                    string badgeId = badge.badgeId;
                    finding.fixes.Add(FixAction.Navigate("Copy awardBadge call", () =>
                    {
                        EditorGUIUtility.systemCopyBuffer =
                            "dp.profile.awardBadge(\"" + EscapeLuaString(badgeId) + "\")";
                    }));
                    findings.Add(finding);
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        private static string EscapeLuaString(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void OpenAsset(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            AssetDatabase.OpenAsset(asset);
        }

        private static bool RemoveManualDraft(string contentId, string badgeId)
        {
            // The saved entry can still carry an old locked source until Merge runs.
            // Establish "manual" from the live Lua scan, not stale persisted metadata.
            var scan = BadgeLuaScanner.Scan(contentId);
            if (scan.discoveries.Any(d => d != null
                && string.Equals(d.badgeId, badgeId, StringComparison.Ordinal)))
                return false;

            var entries = BadgeStore.Load(contentId);
            int removed = entries.RemoveAll(e => e != null
                && string.Equals(e.badgeId, badgeId, StringComparison.Ordinal));
            if (removed == 0) return false;

            BadgeStore.Save(contentId, entries);
            PreUploadCheckRunner.InvalidateCache(contentId);
            return true;
        }
    }
}
#endif
