#if UNITY_EDITOR
using System;
using Defective.JSON;
using UnityEngine.Networking;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{
    // Editor-only client for POST /api/release-tokens — mints the `rlt_...`
    // credential release automation (DreamParkReleaseCommands, AuthAPI's
    // WithReleaseToken) uses instead of a human session bearer. See
    // Assets/DreamPark/RELEASE_AUTOMATION.md.
    //
    // Minting is a one-shot admin/owner action, so this has no player-build
    // reason to exist — same #if UNITY_EDITOR as SDKAPI.cs, whose shape this
    // mirrors.
    public static class ReleaseTokenAPI
    {
        public enum Scope { Core, Sdk, Content }

        public static string ScopeString(Scope scope)
        {
            switch (scope)
            {
                case Scope.Core: return "core";
                case Scope.Sdk: return "sdk";
                case Scope.Content: return "content";
                default: throw new ArgumentOutOfRangeException(nameof(scope));
            }
        }

        // The backend re-derives authorization from `scope` (admin for
        // core/sdk, contentOwners membership for content) independently of
        // whatever the calling panel's own gate already checked — same
        // belt-and-suspenders relationship SDKPublishPanel has with
        // /api/sdk/publish. `contentId` is required and ignored server-side
        // for non-content scopes; validate it client-side before calling.
        //
        // The response's `token` field is the ONLY time the plaintext value
        // ever exists outside the caller's own memory — the server stores
        // just its hash, so there is no "view token" endpoint to add later.
        // Callers must show it once and never persist it themselves.
        public static void Mint(Scope scope, string contentId, string label, int? expiresInDays,
            Action<bool, APIResponse> callback)
        {
            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("scope", ScopeString(scope));
            if (!string.IsNullOrEmpty(contentId)) body.AddField("contentId", contentId);
            if (!string.IsNullOrEmpty(label)) body.AddField("label", label);
            if (expiresInDays.HasValue) body.AddField("expiresInDays", expiresInDays.Value);
            DreamParkAPI.POST("/api/release-tokens", AuthAPI.GetUserAuth(), body, callback);
        }

        // Metadata only — {tokenId, scope, contentId?, label, status,
        // createdAt, lastUsedAt, expiresAt}. tokenId is a separate opaque
        // public id, not derived from the token or its hash.
        public static void List(Action<bool, APIResponse> callback)
        {
            DreamParkAPI.GET("/api/release-tokens", AuthAPI.GetUserAuth(), callback);
        }

        public static void Revoke(string tokenId, Action<bool, APIResponse> callback)
        {
            var body = new JSONObject(JSONObject.Type.Object);
            DreamParkAPI.POST($"/api/release-tokens/{UnityWebRequest.EscapeURL(tokenId ?? "")}/revoke",
                AuthAPI.GetUserAuth(), body, callback);
        }

        // Mirrors SDKAPI.ExtractError — same {error, code} shape Web specified.
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

        // The stable refusal codes Web's contract defines, for callers that
        // want to branch without string-matching `error`.
        public static string ExtractCode(APIResponse response)
        {
            if (response?.json == null || !response.json.HasField("code")) return null;
            return response.json.GetField("code").stringValue;
        }
    }
}
#endif
