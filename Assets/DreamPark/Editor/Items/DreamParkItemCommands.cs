#if UNITY_EDITOR && !DREAMPARKCORE && DREAMPARK_SDK_PACKAGES_READY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;

namespace DreamPark.Items
{
    /// <summary>
    /// Items for agents, through the Unity CLI (unity command --project-path .). The same work
    /// as the Content Uploader's Items section, with no dialogs:
    ///
    ///   dreampark_items_scan   --content_id X [--save true]   what the Lua scan + draft hold
    ///   dreampark_items_push   --content_id X [--ids a,b]     save to the items catalog
    ///   dreampark_items_delete --content_id X --ids a,b       delete catalog items
    ///
    /// Auth = the title's credential (AuthAPI.GetUserAuthFor): the agent key
    /// approved for X (dreampark_agent_request → dreampark_agent_wait), or the
    /// human session. Items also push silently with every Compile & Upload.
    /// </summary>
    public static class DreamParkItemCommands
    {
        [CliCommand("dreampark_items_scan", "List this title's items: the .items.json draft merged with every item id its Lua names (awardItem/awardUniqueItem literals, @var defaults, Inspector values, '-- @item id \"Name\"' lines), plus award calls whose id can't be read. --save true writes the merged draft to Assets/Content/<id>/.items.json.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/items" })]
        public static object Scan(
            [CliArg("content_id", "Content folder under Assets/Content. Default: the only one besides Sample/YOUR_GAME_HERE.")] string contentId = null,
            [CliArg("save", "Write the merged draft to .items.json. Default false.")] bool save = false)
        {
            string error;
            contentId = ResolveContentId(contentId, out error);
            if (contentId == null) return new { success = false, error };

            var scan = ItemLuaScanner.Scan(contentId);
            var items = ItemStore.Merge(ItemStore.Load(contentId), scan.discoveries);
            if (save) ItemStore.Save(contentId, items);
            return new
            {
                success = true,
                contentId,
                draft = ItemStore.PathFor(contentId),
                saved = save,
                scriptsScanned = scan.scriptsScanned,
                prefabsScanned = scan.prefabsScanned,
                items = items.Select(e => new
                {
                    e.itemId, e.name, e.type, e.rarity, e.stackable, e.description, e.iconAssetPath,
                    source = e.Source.ToString(), e.discoveredIn, e.awardedUnique,
                    warning = ItemsSection.StatusOf(e).Key ? ItemsSection.StatusOf(e).Value : null,
                }).ToArray(),
                unresolved = scan.unresolved.Select(u => new { script = u.scriptPath, u.expression }).ToArray(),
            };
        }

        [CliCommand("dreampark_items_push", "Save this title's items (the merged .items.json draft) to the DreamPark items catalog via POST /admin/content/<id>/items/save, icons included. Uses the agent key approved for the title. --ids limits it to some items. --upload_id pushes to a different backend id (e.g. the <id>Beta twin).", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/items" })]
        public static async Task<object> Push(
            [CliArg("content_id", "Content folder under Assets/Content. Default: the only one besides Sample/YOUR_GAME_HERE.")] string contentId = null,
            [CliArg("ids", "Comma-separated item ids to push. Default: every item with an id.")] string ids = null,
            [CliArg("upload_id", "Backend content id to push to. Default: content_id.")] string uploadId = null)
        {
            string error;
            contentId = ResolveContentId(contentId, out error);
            if (contentId == null) return new { success = false, error };

            var scan = ItemLuaScanner.Scan(contentId);
            var items = ItemStore.Merge(ItemStore.Load(contentId), scan.discoveries);
            ItemStore.Save(contentId, items);   // what's pushed == what's on disk

            var wanted = SplitIds(ids);
            if (wanted.Count > 0) items = items.Where(e => wanted.Contains(e.itemId ?? "")).ToList();
            if (items.Count == 0) return new { success = false, contentId, error = "No items to push." };

            string target = string.IsNullOrWhiteSpace(uploadId) ? contentId : uploadId.Trim();
            var tcs = new TaskCompletionSource<ItemUploader.UploadReport>();
            ItemUploader.UploadAll(target, items, false, r => tcs.TrySetResult(r));
            var report = await tcs.Task;
            return new
            {
                success = report.failed == 0 && report.errors.Count == 0,
                contentId = target,
                report.saved,
                report.failed,
                report.skipped,
                savedIds = report.savedIds.ToArray(),
                errors = report.errors.ToArray(),
            };
        }

        [CliCommand("dreampark_items_delete", "Delete items from the DreamPark items catalog (POST /admin/content/<id>/items/<itemId>/delete). Refused for another title's items. Leaves the local .items.json draft alone (a Lua-found id would come straight back). Players who already own the item keep their inventory rows.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/items" })]
        public static async Task<object> Delete(
            [CliArg("ids", "Comma-separated item ids to delete.", Required = true)] string ids,
            [CliArg("content_id", "Backend content id that owns them. Default: the only content folder besides Sample/YOUR_GAME_HERE.")] string contentId = null)
        {
            string error;
            contentId = ResolveContentId(contentId, out error);
            if (contentId == null) return new { success = false, error };
            var list = SplitIds(ids).ToList();
            if (list.Count == 0) return new { success = false, error = "Pass --ids." };

            var tcs = new TaskCompletionSource<ItemUploader.UploadReport>();
            ItemUploader.DeleteAll(contentId, list, r => tcs.TrySetResult(r));
            var report = await tcs.Task;
            return new
            {
                success = report.failed == 0 && report.errors.Count == 0,
                contentId,
                requested = list.Count,
                deleted = report.deleted,
                failed = report.failed,
                errors = report.errors.ToArray(),
            };
        }

        private static HashSet<string> SplitIds(string ids)
        {
            return new HashSet<string>((ids ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ItemStore.SanitizeId).Where(s => s.Length > 0), StringComparer.Ordinal);
        }

        private static string ResolveContentId(string contentId, out string error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(contentId))
            {
                contentId = contentId.Trim();
                if (contentId.IndexOfAny(new[] { '/', '\\' }) >= 0) { error = "content_id must be one folder name."; return null; }
                return contentId;
            }
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Content");
            var candidates = Directory.Exists(root)
                ? Directory.GetDirectories(root).Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith(".") && !n.EndsWith("~")
                                && n != "Sample" && n != "YOUR_GAME_HERE").ToList()
                : new List<string>();
            if (candidates.Count == 1) return candidates[0];
            error = candidates.Count == 0
                ? "No content folder under Assets/Content. Pass --content_id."
                : "Several content folders (" + string.Join(", ", candidates) + "). Pass --content_id.";
            return null;
        }
    }
}
#endif
