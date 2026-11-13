#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Text;
using System.Threading;
using DreamPark.API;
using Defective.JSON;
using Unity.EditorCoroutines.Editor;
using UnityEngine.Networking;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// POST /api/sdk/playlists/generate (DreamPark-Web routes/api.sdk.playlists.routes.js). Same developer auth
    /// as the uploader (AuthAPI.GetUserAuthFor(title): the human session, or the agent key approved for the
    /// title); the model key lives only on Web. A long generation answers 200 + keep-alive spaces + the body, so
    /// success is read from the body's `success`/`code`, never from the status alone.
    /// </summary>
    public static class PlaylistStudioApi
    {
        public const string Path = "/api/sdk/playlists/generate";
        public static readonly string[] RemixPresets = { "jump-gauntlet", "boss-rush", "speed-round" };

        public static string Body(PlaylistStudioSession s, int count, PackagePlaylist remixBase = null, string variant = null, int seed = 0)
        {
            var body = JSONObject.emptyObject;
            body.AddField("schemaVersion", 1);
            body.AddField("mode", remixBase != null ? "remix" : "generate");
            body.AddField("titleId", s.titleId);
            body.AddField("count", count);
            body.AddField("seed", seed);
            body.AddField("context", s.GeneratorContext());
            if (remixBase != null)
            {
                var remix = JSONObject.emptyObject;
                remix.AddField("base", PlaylistStudioJson.WritePlaylist(remixBase, s.titleId));
                remix.AddField("variant", string.IsNullOrWhiteSpace(variant) ? "speed-round" : variant.Trim());
                body.AddField("remix", remix);
            }
            return body.Print();
        }

        /// Editor (non-blocking): calls back on the main thread with the parsed response.
        public static void Send(string titleId, string body, Action<GeneratorResponse> done)
            => EditorCoroutineUtility.StartCoroutineOwnerless(Run(titleId, body, done));

        static IEnumerator Run(string titleId, string body, Action<GeneratorResponse> done)
        {
            using (UnityWebRequest req = Build(titleId, body))
            {
                yield return req.SendWebRequest();
                done?.Invoke(Read(req));
            }
        }

        /// Headless (batchmode / agents): blocks until the answer arrives (a generation takes minutes).
        public static GeneratorResponse SendBlocking(string titleId, string body, int timeoutSec = 900)
        {
            using (UnityWebRequest req = Build(titleId, body))
            {
                req.timeout = timeoutSec;
                var op = req.SendWebRequest();
                while (!op.isDone) Thread.Sleep(100);
                return Read(req);
            }
        }

        static UnityWebRequest Build(string titleId, string body)
        {
            var req = new UnityWebRequest(DreamParkAPI.baseUrl + Path, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 900,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", AuthAPI.GetUserAuthFor(titleId));
            return req;
        }

        static GeneratorResponse Read(UnityWebRequest req)
        {
            string text = req.downloadHandler?.text;
            GeneratorResponse r = GeneratorResponse.Parse(text);
            if (r.code == "BAD_RESPONSE")
            {
                r.code = req.responseCode == 401 ? "UNAUTHORIZED" : "NETWORK";
                r.error = req.responseCode == 401 ? "Sign in (DreamPark → Sign In) to generate playlists."
                    : $"The generator could not be reached ({req.responseCode} {req.error}).";
            }
            return r;
        }
    }
}
#endif
