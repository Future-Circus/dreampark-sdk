// ─────────────────────────────────────────────────────────────────────
//  ItemAwardCheck.cs — the items twin of BadgeAwardCheck.
//
//  Three findings, each with its own subKey mode so ignoring one can never
//  silence another (the rule BadgeAwardCheck cites from DuplicateNamesCheck):
//
//   unwired   Warning  An item card added by hand that no script in the
//                      package awards (no awardItem/awardUniqueItem literal,
//                      @var, Inspector value or -- @item line). Normal mid-
//                      development, worth a look before shipping.
//   singleton Warning  Lua awards the id as a UNIQUE item (awardUniqueItem, or
//                      awardItem with metadata) but the card has Stacks off.
//                      The backend treats a non-stacking catalog item as a
//                      singleton and IGNORES award metadata, so every caught
//                      creature would collapse into one row with no data.
//                      This is the expensive one: it looks fine until a player
//                      catches their second creature.
//   untitled  Info     A Lua-discovered item with no Title yet: the portal,
//                      the profile pages and getItem().name show the raw id.
//
//  Warning, never Blocking: "A check that cannot tell must not block."
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Items;
using UnityEditor;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class ItemAwardCheck : IPreUploadCheck
    {
        public const string CheckId = "item-award";

        private const string ModeUnwired = "unwired";
        private const string ModeSingleton = "singleton";
        private const string ModeUntitled = "untitled";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Items defined and awardable"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // A text scan of the package's Lua plus a prefab pass only when an
        // @var-backed award exists — the same cost as BadgeAwardCheck.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "Items in the Content Uploader's Items section: one added by hand that nothing awards yet, "
                     + "a unique (metadata) item whose catalog entry doesn't stack — the server would drop its "
                     + "per-instance data — or an item with no title.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.contentId))
                return CheckResult.Skipped(CheckId, "no content selected");

            List<ItemStore.Entry> items;
            try
            {
                var scan = ItemLuaScanner.Scan(ctx.contentId);
                items = ItemStore.Merge(ItemStore.Load(ctx.contentId), scan.discoveries);
            }
            catch (Exception e)
            {
                return CheckResult.Errored(CheckId, "item scan failed: " + e.Message);
            }
            if (items.Count == 0) return CheckResult.Clean(CheckId);

            var findings = new List<Finding>();
            string assetPath = ItemStore.PathFor(ctx.contentId);
            string contentId = ctx.contentId;

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.itemId)) continue;
                string itemId = item.itemId;

                if (item.awardedUnique && !item.stackable)
                {
                    var f = new Finding
                    {
                        checkId = CheckId,
                        severity = CheckSeverity.Warning,
                        assetGuid = "",
                        assetPath = assetPath,
                        subKey = ModeSingleton + ":" + itemId,
                        title = "Unique item '" + itemId + "' has Stacks off",
                        detail = "Your Lua awards '" + itemId + "' as a unique item (awardUniqueItem, or awardItem with "
                               + "metadata), but its card has Stacks unticked. A non-stacking catalog item is a singleton: "
                               + "the server ignores award metadata and keeps ONE row. Tick Stacks, then Push Items to Portal.",
                    };
                    f.fixes.Add(new FixAction("Turn Stacks on", () => SetStackable(contentId, itemId))
                    {
                        canBulk = true,
                        affectedPaths = new[] { assetPath },
                        tooltip = "Sets stackable = true on the local item draft. Push Items to Portal to update the catalog.",
                    });
                    findings.Add(f);
                }

                if (!item.IdLocked)
                {
                    var f = new Finding
                    {
                        checkId = CheckId,
                        severity = CheckSeverity.Warning,
                        assetGuid = "",
                        assetPath = assetPath,
                        subKey = ModeUnwired + ":" + itemId,
                        title = "Item '" + itemId + "' isn't awarded by any script yet",
                        detail = "This item was added by hand and no dp.profile.awardItem(\"" + itemId + "\") (or "
                               + "awardUniqueItem, @var, or -- @item line) was found in this package's Lua. Normal "
                               + "mid-development — confirm it's still meant to be wired up before you ship.",
                    };
                    f.fixes.Add(FixAction.Navigate("Copy awardItem call", () =>
                    {
                        EditorGUIUtility.systemCopyBuffer = "dp.profile.awardItem(\"" + Escape(itemId) + "\", 1)";
                    }));
                    f.fixes.Add(new FixAction("Remove item draft", () => RemoveManualDraft(contentId, itemId))
                    {
                        canBulk = false,
                        affectedPaths = new[] { assetPath },
                        confirmTitle = "Remove item draft?",
                        confirmMessage = "Remove the local draft for item '" + itemId + "'?\n\n"
                                       + "This does not delete an item already pushed to the items catalog.",
                        tooltip = "Removes only this hand-authored local item card.",
                    });
                    findings.Add(f);
                }
                else if (string.IsNullOrWhiteSpace(item.name))
                {
                    findings.Add(new Finding
                    {
                        checkId = CheckId,
                        severity = CheckSeverity.Info,
                        assetGuid = "",
                        assetPath = assetPath,
                        subKey = ModeUntitled + ":" + itemId,
                        title = "Item '" + itemId + "' has no title",
                        detail = "Give it a Title (and an icon) in the Content Uploader's Items section — otherwise "
                               + "players see the raw id. " + item.discoveredIn,
                    });
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        private static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool SetStackable(string contentId, string itemId)
        {
            var entries = ItemStore.Load(contentId);
            var e = entries.FirstOrDefault(x => x != null && string.Equals(x.itemId, itemId, StringComparison.Ordinal));
            if (e == null)
            {
                // Discovered but never saved: create the draft row so the fix sticks.
                e = new ItemStore.Entry { itemId = itemId };
                entries.Add(e);
            }
            else if (e.stackable) return false;
            e.stackable = true;
            ItemStore.Save(contentId, entries);
            PreUploadCheckRunner.InvalidateCache(contentId);
            return true;
        }

        private static bool RemoveManualDraft(string contentId, string itemId)
        {
            var scan = ItemLuaScanner.Scan(contentId);
            if (scan.Find(itemId) != null) return false;   // Lua names it — it would come straight back

            var entries = ItemStore.Load(contentId);
            int removed = entries.RemoveAll(x => x != null && string.Equals(x.itemId, itemId, StringComparison.Ordinal));
            if (removed == 0) return false;
            ItemStore.Save(contentId, entries);
            PreUploadCheckRunner.InvalidateCache(contentId);
            return true;
        }
    }
}
#endif
