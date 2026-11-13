#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Text;
using DreamPark.API;
using Defective.JSON;
using Unity.EditorCoroutines.Editor;
using UnityEngine.Networking;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// The Studio's backend calls (repair Oct 20 2026; DreamPark-Web, same developer auth as the uploader):
    /// <list type="bullet">
    /// <item>scripting from the Game Graph — POST /api/sdk/playlists/goals | script | freeplay (routes/api.sdk.playlists.routes.js);</item>
    /// <item>the playlist store (source of truth) — GET /api/content/:id/playlists, GET /api/content/:id/goals,
    /// PUT /api/content/:id/playlists {block, playlists, goals} (routes/playlists.store.routes.js).</item>
    /// </list>
    /// Every callback runs on the main thread with (ok, body, message).
    /// </summary>
    public static class PlaylistStudioBackend
    {
        public delegate void Done(bool ok, JSONObject body, string message);

        public static void GenerateGoals(string titleId, string attraction, string kind, Done done)
        {
            var body = JSONObject.emptyObject;
            body.AddField("titleId", titleId);
            if (!string.IsNullOrEmpty(attraction)) { var a = JSONObject.emptyArray; a.Add(attraction); body.AddField("attractions", a); }
            if (!string.IsNullOrEmpty(kind)) { var k = JSONObject.emptyArray; k.Add(kind); body.AddField("kinds", k); }
            Call("POST", "/api/sdk/playlists/goals", titleId, body, done);
        }

        public static void Script(string titleId, JSONObject playlist, bool force, Done done)
        {
            var body = JSONObject.emptyObject;
            body.AddField("titleId", titleId);
            body.AddField("playlist", playlist);
            if (force) body.AddField("force", true);
            Call("POST", "/api/sdk/playlists/script", titleId, body, done);
        }

        public static void Freeplay(string titleId, JSONObject existing, Done done)
        {
            var body = JSONObject.emptyObject;
            body.AddField("titleId", titleId);
            if (existing != null) body.AddField("existing", existing);
            Call("POST", "/api/sdk/playlists/freeplay", titleId, body, done);
        }

        /// {block, playlists} then {goals} merged into one body.
        public static void Pull(string titleId, Done done)
        {
            Call("GET", $"/api/content/{titleId}/playlists", titleId, null, (ok, lists, msg) =>
            {
                if (!ok) { done(false, lists, msg); return; }
                Call("GET", $"/api/content/{titleId}/goals", titleId, null, (ok2, goals, msg2) =>
                {
                    if (ok2 && goals?.GetField("goals") != null) lists.SetField("goals", goals.GetField("goals"));
                    done(true, lists, ok2 ? null : "goals: " + msg2);
                });
            });
        }

        public static void Push(string titleId, JSONObject body, Done done)
            => Call("PUT", $"/api/content/{titleId}/playlists", titleId, body, done);

        public static void Call(string method, string path, string titleId, JSONObject body, Done done)
            => EditorCoroutineUtility.StartCoroutineOwnerless(Run(method, path, titleId, body, done));

        static IEnumerator Run(string method, string path, string titleId, JSONObject body, Done done)
        {
            using (var req = new UnityWebRequest(DreamParkAPI.baseUrl + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 120 })
            {
                if (body != null) { req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.Print())); req.SetRequestHeader("Content-Type", "application/json"); }
                req.SetRequestHeader("Authorization", AuthAPI.GetUserAuthFor(titleId));
                yield return req.SendWebRequest();
                JSONObject j = null;
                try { j = new JSONObject(req.downloadHandler?.text ?? ""); } catch { j = null; }
                bool ok = req.responseCode >= 200 && req.responseCode < 300 && j != null && j.GetField("success")?.boolValue != false;
                string message = ok ? null
                    : req.responseCode == 401 ? "Sign in (DreamPark → Sign In) first."
                    : j?.GetField("error")?.stringValue != null ? $"{j.GetField("error").stringValue} ({j.GetField("code")?.stringValue ?? req.responseCode.ToString()})"
                    : $"{req.responseCode} {req.error}";
                try { done?.Invoke(ok, j, message); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }
    }
}
#endif
