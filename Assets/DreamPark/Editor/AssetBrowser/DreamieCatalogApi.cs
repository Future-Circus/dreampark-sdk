#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Defective.JSON;
using DreamPark.API;
using Unity.EditorCoroutines.Editor;
using UnityEngine.Networking;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>What the grid is currently asking for.</summary>
    public class DreamieQuery
    {
        public string type = "model";     // the browser is a 3D-model tool by default
        public string search = "";
        public string jobId;              // null = the default view
        public string creatorId;
        public string format = "fbx";     // only show things Unity can actually import
        public bool favoritesOnly;
        public int limit = 60;
        public string cursor;

        public DreamieQuery Clone() => (DreamieQuery)MemberwiseClone();
    }

    /// <summary>
    /// The Asset Browser's half of /api/assets.
    ///
    /// Every call goes through DreamParkAPI, which already routes editor
    /// traffic onto an EditorCoroutine, applies the 30s timeout, and parses
    /// JSON — so this class is only mapping and error translation. Auth is
    /// AuthAPI.GetUserAuth(), which returns the complete "Bearer ..." value;
    /// do not add the scheme.
    /// </summary>
    public static class DreamieCatalogApi
    {
        private static string Esc(string s) => UnityWebRequest.EscapeURL(s ?? "");

        public static string BuildListPath(DreamieQuery q)
        {
            var sb = new StringBuilder("/api/assets?");

            // Favorites is a different QUERY, not a filter — it is ordered by
            // when you starred something, which is not a property of an asset.
            // The server takes the type under `subtype` in that mode.
            if (q.favoritesOnly)
            {
                sb.Append("type=favorites");
                if (!string.IsNullOrEmpty(q.type) && q.type != "all")
                    sb.Append("&subtype=").Append(Esc(q.type));
            }
            else
            {
                sb.Append("type=").Append(Esc(string.IsNullOrEmpty(q.type) ? "all" : q.type));
                if (!string.IsNullOrEmpty(q.jobId)) sb.Append("&job=").Append(Esc(q.jobId));
            }

            if (!string.IsNullOrEmpty(q.search)) sb.Append("&q=").Append(Esc(q.search));
            if (!string.IsNullOrEmpty(q.creatorId)) sb.Append("&creator=").Append(Esc(q.creatorId));
            if (!string.IsNullOrEmpty(q.format)) sb.Append("&format=").Append(Esc(q.format));
            sb.Append("&limit=").Append(Math.Max(1, Math.Min(120, q.limit)));
            if (!string.IsNullOrEmpty(q.cursor)) sb.Append("&cursor=").Append(Esc(q.cursor));

            return sb.ToString();
        }

        /// <summary>
        /// One page of the grid. `onDone(page, error)` — exactly one is null.
        /// </summary>
        public static void List(DreamieQuery q, Action<DreamiePage, string> onDone)
        {
            DreamParkAPI.GET(BuildListPath(q), AuthAPI.GetUserAuth(), (ok, resp) =>
            {
                if (!Ok(ok, resp, out string err)) { onDone?.Invoke(null, err); return; }

                var page = new DreamiePage
                {
                    nextCursor = resp.json.GetField("nextCursor")?.stringValue,
                    degraded = resp.json.GetField("degraded")?.boolValue ?? false,
                };
                var arr = resp.json.GetField("assets");
                if (arr != null && arr.type == JSONObject.Type.Array && arr.list != null)
                    foreach (var node in arr.list)
                    {
                        var a = DreamieAsset.FromJson(node);
                        if (a != null && !string.IsNullOrEmpty(a.id)) page.assets.Add(a);
                    }

                onDone?.Invoke(page, null);
            });
        }

        /// <summary>Type counts, popular tags, and the creator dropdown.</summary>
        public static void Facets(string jobId, Action<List<DreamieCreator>, Dictionary<string, int>, string> onDone)
        {
            string path = "/api/assets/facets" + (string.IsNullOrEmpty(jobId) ? "" : "?job=" + Esc(jobId));
            DreamParkAPI.GET(path, AuthAPI.GetUserAuth(), (ok, resp) =>
            {
                if (!Ok(ok, resp, out string err)) { onDone?.Invoke(null, null, err); return; }

                var creators = new List<DreamieCreator>();
                var arr = resp.json.GetField("creators");
                if (arr != null && arr.type == JSONObject.Type.Array && arr.list != null)
                    foreach (var node in arr.list)
                    {
                        var c = DreamieCreator.FromJson(node);
                        if (c != null && !string.IsNullOrEmpty(c.creatorId)) creators.Add(c);
                    }

                var counts = new Dictionary<string, int>();
                var countsNode = resp.json.GetField("counts");
                if (countsNode != null && countsNode.type == JSONObject.Type.Object && countsNode.keys != null)
                    for (int i = 0; i < countsNode.keys.Count; i++)
                        counts[countsNode.keys[i]] = countsNode.list[i]?.intValue ?? 0;

                onDone?.Invoke(creators, counts, null);
            });
        }

        /// <summary>The batch folders, for the job dropdown.</summary>
        public static void Jobs(Action<List<DreamieJob>, string> onDone)
        {
            DreamParkAPI.GET("/api/assets/jobs?limit=100", AuthAPI.GetUserAuth(), (ok, resp) =>
            {
                if (!Ok(ok, resp, out string err)) { onDone?.Invoke(null, err); return; }

                var jobs = new List<DreamieJob>();
                var arr = resp.json.GetField("jobs");
                if (arr != null && arr.type == JSONObject.Type.Array && arr.list != null)
                    foreach (var node in arr.list)
                    {
                        var j = DreamieJob.FromJson(node);
                        if (j != null && !string.IsNullOrEmpty(j.id)) jobs.Add(j);
                    }

                onDone?.Invoke(jobs, null);
            });
        }

        /// <summary>
        /// Star / unstar. PUT and DELETE rather than a POST /toggle so a
        /// double-click settles on the state the person asked for instead of
        /// racing itself — the server is built the same way.
        /// </summary>
        public static void SetFavorite(string assetId, bool on, Action<bool> onDone)
        {
            string url = DreamParkAPI.baseUrl + "/api/assets/" + Esc(assetId) + "/favorite";
            EditorCoroutineUtility.StartCoroutineOwnerless(FavoriteRoutine(url, on, onDone));
        }

        private static IEnumerator FavoriteRoutine(string url, bool on, Action<bool> onDone)
        {
            // DreamParkAPI has no PUT-with-auth or DELETE verb, and adding one
            // for a two-line call would change a file that must stay in sync
            // with dreampark-core. Done inline instead.
            using (var req = new UnityWebRequest(url, on ? UnityWebRequest.kHttpVerbPUT : UnityWebRequest.kHttpVerbDELETE))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 15;
                string auth = AuthAPI.GetUserAuth();
                if (!string.IsNullOrEmpty(auth)) req.SetRequestHeader("Authorization", auth);
                yield return req.SendWebRequest();
                onDone?.Invoke(req.result == UnityWebRequest.Result.Success);
            }
        }

        /// <summary>
        /// Count a download without fetching the bytes twice.
        ///
        /// The bytes come straight from the public bucket URL on the row — the
        /// /download endpoint exists to increment the counter and then 302 to
        /// that same URL, and following it with an Authorization header
        /// attached would send a DreamPark credential to Google's storage
        /// host, which rejects it. So the counter is pinged with a HEAD (which
        /// Express routes to the same GET handler, so recordDownload still
        /// runs) and the transfer is done separately and unauthenticated.
        ///
        /// Fire and forget: a missed count must never fail an import.
        /// </summary>
        public static void RecordDownload(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return;
            string url = DreamParkAPI.baseUrl + "/api/assets/" + Esc(assetId) + "/download";
            EditorCoroutineUtility.StartCoroutineOwnerless(RecordDownloadRoutine(url));
        }

        private static IEnumerator RecordDownloadRoutine(string url)
        {
            using (var req = UnityWebRequest.Head(url))
            {
                req.timeout = 10;
                req.redirectLimit = 0; // the counter already ran; the 302 target is not wanted
                string auth = AuthAPI.GetUserAuth();
                if (!string.IsNullOrEmpty(auth)) req.SetRequestHeader("Authorization", auth);
                yield return req.SendWebRequest();
            }
        }

        /* ── error translation ─────────────────────────────────────────────
         *
         * Raw transport errors are useless to a creator. The three that
         * actually happen each get said in the terms the person can act on,
         * and everything else falls through with whatever the server offered.
         * ─────────────────────────────────────────────────────────────── */

        private static bool Ok(bool ok, DreamParkAPI.APIResponse resp, out string error)
        {
            error = null;
            if (resp != null && resp.statusCode == 401)
            {
                error = "Your DreamPark session has expired. Sign in again (DreamPark → Sign In).";
                return false;
            }
            if (!ok || resp == null)
            {
                error = resp?.error ?? "Could not reach DreamPark.";
                return false;
            }
            if (resp.json == null)
            {
                error = "DreamPark returned something that was not JSON (HTTP " + resp.statusCode + ").";
                return false;
            }
            if (resp.json.GetField("success")?.boolValue != true)
            {
                // The server hands back a real sentence when an index is still
                // building, which is the single most likely failure on a fresh
                // deploy. Prefer it over anything invented here.
                error = resp.json.GetField("error")?.stringValue ?? "The asset library refused that request.";
                return false;
            }
            return true;
        }
    }
}
#endif
