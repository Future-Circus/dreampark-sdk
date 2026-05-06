#if UNITY_EDITOR
using System;
using System.Collections;
using Defective.JSON;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{
    // Editor-only API client for the SDK self-update endpoints. The manifest
    // call mirrors any other auth'd GET; the publish call needs custom multipart
    // handling because the body mixes a binary .unitypackage with text fields.
    public static class SDKAPI
    {
        public static void GetManifest(Action<bool, APIResponse> callback)
        {
            DreamParkAPI.GET("/api/sdk/manifest", AuthAPI.GetUserAuth(), (success, response) =>
            {
                callback?.Invoke(success, response);
            });
        }

        public static void GetDownloadUrl(string version, Action<bool, APIResponse> callback)
        {
            DreamParkAPI.GET($"/api/sdk/download/{version}", AuthAPI.GetUserAuth(), (success, response) =>
            {
                callback?.Invoke(success, response);
            });
        }

        // UI-hint endpoint. Server returns { canPublish: bool } — true iff the
        // caller is an admin. The actual publish endpoint independently enforces
        // admin access, so a non-admin who somehow bypasses the menu disable
        // still hits a 403 at submit time.
        public static void CheckCanPublish(Action<bool, APIResponse> callback)
        {
            DreamParkAPI.GET("/api/sdk/canPublish", AuthAPI.GetUserAuth(), (success, response) =>
            {
                callback?.Invoke(success, response);
            });
        }

        // Multipart POST: { package: <file>, version: <text>, releaseNotes: <text> }.
        // Backend gates with `requireAdminJson`; SDK shows the 403 message verbatim.
        public static void PublishVersion(string version, string releaseNotes, byte[] packageBytes, string fileName, Action<bool, APIResponse> callback)
        {
            if (packageBytes == null || packageBytes.Length == 0)
            {
                callback?.Invoke(false, new APIResponse(false, 0, "Package bytes are empty."));
                return;
            }

            EditorCoroutineUtility.StartCoroutineOwnerless(
                PublishCoroutine(version, releaseNotes, packageBytes, fileName, callback)
            );
        }

        private static IEnumerator PublishCoroutine(string version, string releaseNotes, byte[] packageBytes, string fileName, Action<bool, APIResponse> callback)
        {
            var form = new WWWForm();
            form.AddField("version", version ?? "");
            form.AddField("releaseNotes", releaseNotes ?? "");
            form.AddBinaryData("package", packageBytes, fileName ?? "sdk.unitypackage", "application/octet-stream");

            string url = DreamParkAPI.devBaseUrl + "/api/sdk/publish";
            using (var req = UnityWebRequest.Post(url, form))
            {
                req.SetRequestHeader("Authorization", AuthAPI.GetUserAuth());
                yield return req.SendWebRequest();

                bool success = !(req.result == UnityWebRequest.Result.ConnectionError ||
                                 req.result == UnityWebRequest.Result.ProtocolError);
                long code = req.responseCode;
                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                string error = success ? null : req.error;
                var apiResponse = new APIResponse(success, code, error, raw);
                callback?.Invoke(success, apiResponse);
            }
        }

        // Helper for callers (popup, panel) — extracts a server-provided error
        // message out of the response, falling back to the network-level error.
        public static string ExtractError(APIResponse response, string fallback = "Unknown error")
        {
            if (response == null) return fallback;
            if (response.json != null && response.json.HasField("error"))
            {
                var msg = response.json.GetField("error").stringValue;
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
            return string.IsNullOrEmpty(response.error) ? fallback : response.error;
        }
    }
}
#endif
