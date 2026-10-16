#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using Defective.JSON;
using DreamPark.API;
using DreamPark.Badges;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.Items
{
    // Pushes the Content Uploader's item cards to the items catalog — the item
    // twin of BadgeUploader, on the SAME owner-authorized route family the
    // /developer/content/:id/items page uses:
    //
    //   POST /admin/content/:contentId/items/save            (multipart/form-data)
    //     entryId · name · description · type · rarity
    //     stackable ("true"|"false") + stackableSubmitted=1  (the web form's
    //                 checkbox marker — without it the server leaves stacking alone)
    //     icon      optional image part (PNG/JPEG/WebP/GIF, ≤ 4 MB)
    //   POST /admin/content/:contentId/items/:itemId/delete   (JSON answer when asked)
    //
    // Auth is the title's credential (AuthAPI.GetUserAuthFor): a human session,
    // or the agent key approved for this title — the agent scope already allows
    // items/save, items/:id/delete and items/:id/upload-icon.
    //
    // Item ids are GLOBAL catalog doc ids (items/{itemId}), unlike badges'
    // `{contentId}__{shortId}`: an inventory row keys on the catalog id, so
    // namespacing items would need the award path on every client to resolve
    // short ids first. The server instead REFUSES a save or delete that would
    // touch another title's item ("already exists under a different content").
    // Prefixing your ids (cr_fox, zz_medkit) keeps you clear of that.
    //
    // Same two traps as BadgeUploader, same fixes: the route answers HTML by
    // default (so we send Accept: application/json + X-Requested-With and trust
    // the JSON `success`), and an expired session 302s to /login (so
    // redirectLimit = 0 and a 3xx is reported as an auth failure).
    public static class ItemUploader
    {
        public class UploadReport
        {
            public int saved;
            public int failed;
            public int skipped;
            public int deleted;
            public List<string> errors = new List<string>();
            public List<string> savedIds = new List<string>();
        }

        /// Saves every item card that has an id. onDone runs exactly once, on
        /// the editor thread.
        public static void UploadAll(string contentId, List<ItemStore.Entry> items,
                                     bool interactive, Action<UploadReport> onDone = null)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(
                UploadAllRoutine(contentId, items, interactive, onDone));
        }

        /// Deletes catalog items by id (not local drafts). A missing item is
        /// not an error (the server answers deleted=false).
        public static void DeleteAll(string contentId, List<string> itemIds, Action<UploadReport> onDone = null)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(DeleteAllRoutine(contentId, itemIds, onDone));
        }

        private static IEnumerator UploadAllRoutine(string contentId, List<ItemStore.Entry> items,
                                                    bool interactive, Action<UploadReport> onDone)
        {
            var report = new UploadReport();
            string auth = AuthAPI.GetUserAuthFor(contentId);
            if (string.IsNullOrEmpty(contentId) || items == null || items.Count == 0)
            {
                onDone?.Invoke(report);
                yield break;
            }
            if (string.IsNullOrEmpty(auth) || !AuthAPI.isLoggedIn)
            {
                report.errors.Add("Not signed in to DreamPark.");
                if (interactive)
                    EditorUtility.DisplayDialog("Not signed in", "Sign in to DreamPark before pushing items.", "OK");
                onDone?.Invoke(report);
                yield break;
            }

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    string id = item != null ? ItemStore.SanitizeId(item.itemId) : "";
                    if (id.Length == 0)
                    {
                        report.skipped++;
                        continue;
                    }

                    if (interactive)
                    {
                        EditorUtility.DisplayProgressBar("Pushing items",
                            $"Saving '{id}' ({i + 1}/{items.Count})...", (float)i / Mathf.Max(1, items.Count));
                    }

                    // Sequential: one Firestore doc per call and a readable log.
                    string error = null;
                    yield return SaveOne(contentId, auth, item, id, e => error = e);

                    if (error == null) { report.saved++; report.savedIds.Add(id); }
                    else
                    {
                        report.failed++;
                        report.errors.Add($"{id}: {error}");
                        Debug.LogWarning($"[Items] '{id}' failed: {error}");
                    }
                }
            }
            finally
            {
                if (interactive) EditorUtility.ClearProgressBar();
            }

            if (report.saved > 0)
                Debug.Log($"[Items] {report.saved} item(s) saved to the items catalog for {contentId}.");

            if (interactive)
            {
                if (report.failed == 0 && report.saved > 0)
                    EditorUtility.DisplayDialog("Items pushed",
                        $"{report.saved} item(s) saved. They're live on your developer portal under Items.", "OK");
                else if (report.failed > 0)
                    EditorUtility.DisplayDialog("Some items failed",
                        $"{report.saved} saved, {report.failed} failed.\n\n" + string.Join("\n", report.errors.ToArray()), "OK");
                else
                    EditorUtility.DisplayDialog("Nothing to push",
                        "Give each item an ID before pushing — the ID is what the catalog and every award key on.", "OK");
            }

            onDone?.Invoke(report);
        }

        private static IEnumerator SaveOne(string contentId, string auth, ItemStore.Entry item, string id,
                                           Action<string> onResult)
        {
            var form = new WWWForm();
            form.AddField("entryId", id);
            // Sent even when empty: the handler writes a field only when it is
            // present, so omitting a cleared value would keep the old one.
            form.AddField("name", item.name ?? "");
            form.AddField("description", item.description ?? "");
            form.AddField("type", item.type ?? "");
            form.AddField("rarity", item.rarity ?? "");
            form.AddField("stackableSubmitted", "1");
            form.AddField("stackable", item.stackable ? "true" : "false");

            if (!string.IsNullOrEmpty(item.iconAssetPath))
            {
                string fileName, mimeType, iconError;
                byte[] icon = BadgeUploader.ReadIconBytes(item.iconAssetPath, out fileName, out mimeType, out iconError);
                if (icon != null) form.AddBinaryData("icon", icon, fileName, mimeType);
                else if (!string.IsNullOrEmpty(iconError))
                    Debug.LogWarning($"[Items] '{id}' icon skipped: {iconError}");
            }

            string url = DreamParkAPI.baseUrl + "/admin/content/" + Uri.EscapeDataString(contentId) + "/items/save";
            using (var req = UnityWebRequest.Post(url, form))
            {
                Prepare(req, auth);
                yield return req.SendWebRequest();
                onResult(InterpretResponse(req));
            }
        }

        private static IEnumerator DeleteAllRoutine(string contentId, List<string> itemIds, Action<UploadReport> onDone)
        {
            var report = new UploadReport();
            string auth = AuthAPI.GetUserAuthFor(contentId);
            if (string.IsNullOrEmpty(contentId) || itemIds == null || itemIds.Count == 0) { onDone?.Invoke(report); yield break; }
            if (string.IsNullOrEmpty(auth) || !AuthAPI.isLoggedIn)
            {
                report.errors.Add("Not signed in to DreamPark.");
                onDone?.Invoke(report);
                yield break;
            }

            foreach (var raw in itemIds)
            {
                string id = ItemStore.SanitizeId(raw);
                if (id.Length == 0) { report.skipped++; continue; }
                string url = DreamParkAPI.baseUrl + "/admin/content/" + Uri.EscapeDataString(contentId)
                    + "/items/" + Uri.EscapeDataString(id) + "/delete";
                var form = new WWWForm();
                form.AddField("entryId", id);
                using (var req = UnityWebRequest.Post(url, form))
                {
                    Prepare(req, auth);
                    yield return req.SendWebRequest();
                    string error = InterpretResponse(req);
                    if (error != null)
                    {
                        report.failed++;
                        report.errors.Add($"{id}: {error}");
                        continue;
                    }
                    report.saved++;
                    bool deleted = false;
                    try { deleted = new JSONObject(req.downloadHandler.text).GetField("deleted")?.boolValue ?? false; }
                    catch { }
                    if (deleted) report.deleted++;
                }
            }
            onDone?.Invoke(report);
        }

        private static void Prepare(UnityWebRequest req, string auth)
        {
            req.timeout = 60;
            req.SetRequestHeader("Authorization", auth);
            req.SetRequestHeader("Accept", "application/json");
            req.SetRequestHeader("X-Requested-With", "XMLHttpRequest");
            req.redirectLimit = 0;
        }

        // null on success, else a human-readable error. Same reading as
        // BadgeUploader: trust the JSON `success`, name a 3xx as auth.
        internal static string InterpretResponse(UnityWebRequest req)
        {
            long code = req.responseCode;
            if (req.result == UnityWebRequest.Result.ConnectionError)
                return "network error: " + req.error;
            if (code >= 300 && code < 400)
                return "session expired or not authorized — log out and back in (DreamPark ▸ Sign In).";

            string body = null;
            try { body = req.downloadHandler != null ? req.downloadHandler.text : null; }
            catch { }

            if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("{"))
            {
                try
                {
                    var json = new JSONObject(body);
                    bool ok = json.HasField("success") && json["success"].boolValue;
                    if (ok) return null;
                    string err = json.HasField("error") ? json["error"].stringValue : null;
                    if (!string.IsNullOrEmpty(err) && json.HasField("code") && json["code"].stringValue == "AGENT_KEY_SCOPE")
                        return err + " (this agent key can't reach that route)";
                    return string.IsNullOrEmpty(err) ? $"refused (HTTP {code})" : err;
                }
                catch { }
            }

            if (code == 403) return "not an owner of this content (403).";
            if (code >= 400) return $"HTTP {code}" + (string.IsNullOrEmpty(req.error) ? "" : " — " + req.error);
            return "server returned a web page instead of JSON — the save was not confirmed.";
        }
    }
}
#endif
