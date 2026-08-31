#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Defective.JSON;
using DreamPark.API;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.Badges
{
    // Pushes the Content Uploader's badge cards to the developer portal.
    //
    // ── The endpoint already exists and is already owner-authorized ─────────
    //
    //   POST /admin/content/:contentId/badges/save        (multipart/form-data)
    //     entryId      — the badge id. REQUIRED. Server-side sanitizeEntryId
    //                    strips everything outside [A-Za-z0-9_-] and caps at 80.
    //     name         — badge title
    //     description  — badge description
    //     icon         — optional image part
    //
    // This is the SAME endpoint the /developer/content/:id/badges web page
    // posts to. It is NOT staff-only: admin.content.routes.js mounts
    // authorizeContentAccess, which fast-paths any uid in the content doc's
    // contentOwners array. Nothing new was needed on the backend.
    //
    // ── Two traps this file exists to avoid ─────────────────────────────────
    //
    //  1. THE ROUTE ANSWERS HTML BY DEFAULT. Without an XHR-ish header the
    //     handler 302s back to the admin badges page on success AND on failure,
    //     and UnityWebRequest follows redirects, so we would read a 200 with an
    //     HTML body and call every outcome a success. We send
    //     `Accept: application/json` + `X-Requested-With: XMLHttpRequest`, which
    //     flips the handler's `wantsJson` branch, and we assert on the JSON's
    //     own `success` field rather than on the status code.
    //
    //  2. AN EXPIRED SESSION ALSO REDIRECTS. verifySessionBearerRedirect sends
    //     302 → /login for a bad token — no 401 — so a followed redirect would
    //     return the login PAGE with status 200. redirectLimit = 0 keeps the 302
    //     visible so we can name it as an auth failure instead of reporting a
    //     phantom success.
    public static class BadgeUploader
    {
        // Matches the backend's ICON_MAX_BYTES so an oversized icon is refused
        // here, where the developer can see which badge it was, instead of after
        // a 4 MB round trip.
        private const int MaxIconBytes = 4 * 1024 * 1024;

        public class UploadReport
        {
            public int saved;
            public int failed;
            public int skipped;                     // no id — nothing to key on
            public List<string> errors = new List<string>();
        }

        /// Uploads every badge card that has an id. Fire-and-forget from the
        /// upload flow, or driven by the panel's "Push Badges to Portal" button.
        /// onDone is invoked exactly once, on the editor thread.
        public static void UploadAll(string contentId, List<BadgeStore.Entry> badges,
                                     bool interactive, Action<UploadReport> onDone = null)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(
                UploadAllRoutine(contentId, badges, interactive, onDone));
        }

        private static IEnumerator UploadAllRoutine(string contentId, List<BadgeStore.Entry> badges,
                                                    bool interactive, Action<UploadReport> onDone)
        {
            var report = new UploadReport();

            string auth = AuthAPI.GetUserAuth();
            if (string.IsNullOrEmpty(contentId) || badges == null || badges.Count == 0)
            {
                onDone?.Invoke(report);
                yield break;
            }
            if (string.IsNullOrEmpty(auth) || !AuthAPI.isLoggedIn)
            {
                report.errors.Add("Not signed in to DreamPark.");
                if (interactive)
                    EditorUtility.DisplayDialog("Not signed in", "Sign in to DreamPark before pushing badges.", "OK");
                onDone?.Invoke(report);
                yield break;
            }

            try
            {
                for (int i = 0; i < badges.Count; i++)
                {
                    var badge = badges[i];
                    if (badge == null || string.IsNullOrEmpty((badge.badgeId ?? "").Trim()))
                    {
                        report.skipped++;
                        continue;
                    }

                    if (interactive)
                    {
                        EditorUtility.DisplayProgressBar(
                            "Pushing badges",
                            $"Saving '{badge.badgeId}' ({i + 1}/{badges.Count})...",
                            (float)i / Mathf.Max(1, badges.Count));
                    }

                    // Sequential on purpose: the handler writes one Firestore doc
                    // per call and the developer wants an ordered, readable log,
                    // not N interleaved failures.
                    string error = null;
                    yield return SaveOne(contentId, auth, badge, e => error = e);

                    if (error == null)
                    {
                        report.saved++;
                    }
                    else
                    {
                        report.failed++;
                        report.errors.Add($"{badge.badgeId}: {error}");
                        Debug.LogWarning($"[Badges] '{badge.badgeId}' failed: {error}");
                    }
                }
            }
            finally
            {
                if (interactive) EditorUtility.ClearProgressBar();
            }

            if (report.saved > 0)
                Debug.Log($"[Badges] {report.saved} badge(s) saved to the developer portal for {contentId}.");

            if (interactive)
            {
                if (report.failed == 0 && report.saved > 0)
                {
                    EditorUtility.DisplayDialog(
                        "Badges pushed",
                        $"{report.saved} badge(s) saved. They're live on your developer portal under Badges.",
                        "OK");
                }
                else if (report.failed > 0)
                {
                    EditorUtility.DisplayDialog(
                        "Some badges failed",
                        $"{report.saved} saved, {report.failed} failed.\n\n"
                            + string.Join("\n", report.errors.ToArray()),
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Nothing to push",
                        "Give each badge an ID before pushing — the ID is what the backend keys the badge on.",
                        "OK");
                }
            }

            onDone?.Invoke(report);
        }

        private static IEnumerator SaveOne(string contentId, string auth, BadgeStore.Entry badge,
                                           Action<string> onResult)
        {
            var form = new WWWForm();
            form.AddField("entryId", (badge.badgeId ?? "").Trim());
            // Sent unconditionally, including when empty: the handler only writes
            // a field when `req.body[field] !== undefined`, so omitting a cleared
            // title would silently keep the old one on the backend and the panel
            // would be lying about what the portal holds.
            form.AddField("name", badge.name ?? "");
            form.AddField("description", badge.description ?? "");

            if (!string.IsNullOrEmpty(badge.iconAssetPath))
            {
                string fileName, mimeType, iconError;
                byte[] icon = ReadIconBytes(badge.iconAssetPath, out fileName, out mimeType, out iconError);
                if (icon != null)
                {
                    form.AddBinaryData("icon", icon, fileName, mimeType);
                }
                else if (!string.IsNullOrEmpty(iconError))
                {
                    // Don't abandon the badge over its picture — the id, title and
                    // description are the parts a player actually needs. Warn and
                    // save the rest.
                    Debug.LogWarning($"[Badges] '{badge.badgeId}' icon skipped: {iconError}");
                }
            }

            string url = DreamParkAPI.baseUrl
                + "/admin/content/" + Uri.EscapeDataString(contentId) + "/badges/save";

            using (var req = UnityWebRequest.Post(url, form))
            {
                req.timeout = 60;
                // See the class comment: without these two headers the route
                // answers with a redirect to an HTML page and every outcome
                // arrives looking like a success.
                req.SetRequestHeader("Authorization", auth);
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("X-Requested-With", "XMLHttpRequest");
                req.redirectLimit = 0;

                yield return req.SendWebRequest();

                onResult(InterpretResponse(req));
            }
        }

        // Returns null on success, or a human-readable error.
        private static string InterpretResponse(UnityWebRequest req)
        {
            long code = req.responseCode;

            if (req.result == UnityWebRequest.Result.ConnectionError)
                return "network error: " + req.error;

            if (code >= 300 && code < 400)
            {
                // The only redirect this route emits is the auth bounce to
                // /login. Naming it beats "unexpected status 302".
                return "session expired or not authorized — log out and back in (DreamPark ▸ Sign In).";
            }

            string body = null;
            try { body = req.downloadHandler != null ? req.downloadHandler.text : null; }
            catch { }

            // The handler's JSON branch reports its own outcome; trust that over
            // the status code, which is 200 for both a save and (in the HTML
            // branch) a redirect that got followed.
            if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("{"))
            {
                try
                {
                    var json = new JSONObject(body);
                    bool ok = json.HasField("success") && json["success"].boolValue;
                    if (ok) return null;
                    string err = json.HasField("error") ? json["error"].stringValue : null;
                    return string.IsNullOrEmpty(err) ? $"save refused (HTTP {code})" : err;
                }
                catch { /* fall through to the status-code read */ }
            }

            if (code == 403) return "not an owner of this content (403).";
            if (code >= 400) return $"HTTP {code}" + (string.IsNullOrEmpty(req.error) ? "" : " — " + req.error);

            // 2xx with a non-JSON body means the wantsJson branch didn't fire —
            // an HTML page came back. Refuse to call that a save.
            return "server returned a web page instead of JSON — the save was not confirmed.";
        }

        // Same shape as the panel's logo reader: prefer the ORIGINAL bytes on
        // disk (no Read/Write import flag needed, no re-encode), fall back to a
        // blit + PNG encode for source formats the backend won't take (PSD, TGA)
        // or textures that aren't readable.
        public static byte[] ReadIconBytes(string assetPath, out string fileName, out string mimeType,
                                           out string error)
        {
            fileName = null; mimeType = null; error = null;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex == null)
            {
                error = "icon asset not found at " + assetPath;
                return null;
            }

            string ext = Path.GetExtension(assetPath ?? "").ToLowerInvariant();
            string directMime = null;
            if (ext == ".png") directMime = "image/png";
            else if (ext == ".jpg" || ext == ".jpeg") directMime = "image/jpeg";
            else if (ext == ".webp") directMime = "image/webp";
            else if (ext == ".gif") directMime = "image/gif";

            if (directMime != null && File.Exists(assetPath))
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(assetPath);
                    if (raw.Length > MaxIconBytes)
                    {
                        error = $"icon is {raw.Length / (1024 * 1024)} MB; the limit is 4 MB.";
                        return null;
                    }
                    fileName = Path.GetFileName(assetPath);
                    mimeType = directMime;
                    return raw;
                }
                catch (Exception e)
                {
                    error = "could not read " + assetPath + ": " + e.Message;
                    // fall through to the encode path
                }
            }

            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                byte[] png = readable.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(readable);

                if (png == null || png.Length == 0)
                {
                    error = "could not encode the icon to PNG.";
                    return null;
                }
                if (png.Length > MaxIconBytes)
                {
                    error = $"icon encodes to {png.Length / (1024 * 1024)} MB; the limit is 4 MB.";
                    return null;
                }

                fileName = tex.name + ".png";
                mimeType = "image/png";
                error = null;
                return png;
            }
            catch (Exception e)
            {
                error = "PNG encode failed: " + e.Message;
                return null;
            }
        }
    }
}
#endif
