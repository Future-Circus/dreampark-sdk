using UnityEngine;
using UnityEngine.Networking;
using Defective.JSON;
using System;
using System.Collections;
using System.Collections.Generic;

#if UNITY_EDITOR
using Unity.EditorCoroutines.Editor;
#endif

namespace DreamPark.API
{
        public class DreamParkAPI
        {
            public class APIResponse
            {
            public bool success;
            public JSONObject json;
            public byte[] data;
            public long statusCode;
            public string rawText;
            public string error;
            /// The response's ETag header verbatim (quoted, per RFC 7232),
            /// null when the server sent none. Opaque — store it, offer it
            /// back via GET's ifNoneMatch, never inspect it.
            public string etag;

                public APIResponse(bool success, long code, string error, string rawText = "", byte[] data = null)
                {
                    this.success = success;
                    this.statusCode = code;
                    this.rawText = rawText ?? "";
                    this.error = error;
                    this.json = LooksLikeJson(this.rawText) ? new JSONObject(this.rawText) : null;
                    this.data = data ?? null;
                }

                private static bool LooksLikeJson(string text)
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return false;
                    }

                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (char.IsWhiteSpace(c))
                        {
                            continue;
                        }

                        return c == '{' || c == '[';
                    }

                    return false;
                }
            }
        
        // ── API host (qrcade rename, DreamPark-Web docs/contracts/qr-hosts.md §5) ────────────────
        /// The legacy host. It serves the same API forever (shipped builds have it baked in) and it is
        /// the website: pages the app shows or opens, and every path that is not an API path.
        public const string LegacyBaseUrl = "https://dreampark.app";
        /// The API host new player builds call: the same paths (/app /api /auth /internal /storage).
        public const string ApiHostBaseUrl = "https://api.qrcd.is";

#if DREAMPARKCORE && !UNITY_EDITOR
        [Tooltip("Optional: override base URLs at runtime.")]
        public static string prodBaseUrl = ApiHostBaseUrl;
#else
        // Editors (SDK and core) stay on dreampark.app: the uploader bakes baseUrl into every catalog's
        // bundle URLs, which shipped clients with no fallback must be able to load forever.
        [Tooltip("Optional: override base URLs at runtime.")]
        public static string prodBaseUrl = LegacyBaseUrl;
#endif

        // The dev backend (dreampark-dev on Heroku) was retired Oct 2026, so
        // every build talks to prod. devBaseUrl survives only so creator code
        // that still references it compiles; it points at prod.
        [Obsolete("The dev backend is retired. Use DreamParkAPI.baseUrl.")]
        public static string devBaseUrl = prodBaseUrl;

        public static string baseUrl = prodBaseUrl;

        /// True once this app session moved off the API host after a transport failure. It never moves back:
        /// dreampark.app serves the same API forever.
        public static bool OnLegacyFallback { get; private set; }

        /// A finished request to the API host that failed in TRANSPORT (DNS, connect, TLS, timeout: no HTTP status
        /// at all; a status means the server answered and NEVER falls back): this session moves to dreampark.app
        /// (baseUrl switches, so every later request built from it goes there) and the same request's URL there is
        /// returned, to retry ONCE. Null = not a fallback case (another host, an answer, an abort). Code that sends
        /// its own UnityWebRequest on baseUrl passes the finished request here even when it does not retry.
        public static string FallbackUrlAfter(UnityWebRequest req)
        {
            if (req == null || !IsTransportFailure(req)) return null;
            return FallbackUrlFor(req.url, req.error);
        }

        /// No HTTP answer at all (not an abort we asked for).
        public static bool IsTransportFailure(UnityWebRequest req) =>
            req != null && req.result == UnityWebRequest.Result.ConnectionError && req.responseCode <= 0 &&
            !string.Equals(req.error, "Request aborted", StringComparison.OrdinalIgnoreCase);

        /// May a request that failed in TRANSPORT on the API host be sent again on dreampark.app (qr-hosts.md §5)? PURE.
        /// GET/HEAD: yes, always. Any other method (a write — POST, PUT, DELETE, PATCH, multipart): only when the request
        /// certainly never reached the server — DNS, connect or TLS failure — because both hosts are the same server and
        /// a write that timed out or lost its connection may already have been applied. UnityWebRequest only says
        /// ConnectionError, so this reads the error text Unity gives on Android (libcurl) and iOS (NSURLSession); anything
        /// it does not recognise is NOT retried on a write. The session switches hosts either way.
        public static bool MayRetryOnFallback(string method, string error)
        {
            string m = (method ?? "").Trim().ToUpperInvariant();
            if (m == "GET" || m == "HEAD") return true;
            string e = (error ?? "").ToLowerInvariant();
            if (e.Length == 0) return false;
            // A timeout, a dropped/reset connection, a send/receive failure: the server may have the request.
            foreach (string maybe in MaybeReachedErrorText)
                if (e.Contains(maybe)) return false;
            foreach (string never in NeverReachedErrorText)
                if (e.Contains(never)) return true;
            return false;
        }

        static readonly string[] MaybeReachedErrorText =
        {
            "timeout", "timed out", "reset", "connection was lost", "connection lost", "aborted", "closed",
            "broken pipe", "failed to receive", "failed to send", "receive failure", "send failure", "no data",
            "empty reply", "unknown error",
        };

        static readonly string[] NeverReachedErrorText =
        {
            // DNS — Unity "Cannot resolve destination host"; libcurl "Could not resolve host"; iOS "A server with the
            // specified hostname could not be found."; getaddrinfo text.
            "resolve", "hostname could not be found", "unknown host", "nodename nor servname", "name or service not known",
            "no address associated",
            // Connect — Unity "Cannot connect to destination host"; libcurl "Failed to connect"; iOS "Could not connect to
            // the server." / "The Internet connection appears to be offline."
            "cannot connect", "could not connect", "failed to connect", "couldn't connect", "connection refused",
            "network is unreachable", "no route to host", "appears to be offline", "not connected to the internet",
            // TLS — Unity "Unable to complete SSL connection" / "SSL CA certificate error"; iOS "An SSL error has occurred…",
            // "The certificate for this server is invalid…".
            "ssl", "tls", "certificate", "secure connection", "handshake",
        };

        /// <see cref="FallbackUrlAfter"/> for a URL that failed in transport: switches the session and returns the URL
        /// on dreampark.app, or null when the URL is not on the API host.
        public static string FallbackUrlFor(string url, string why = null)
        {
            if (!IsOnApiHost(url)) return null;
            if (string.Equals(baseUrl, ApiHostBaseUrl, StringComparison.OrdinalIgnoreCase)) baseUrl = LegacyBaseUrl;
            if (!OnLegacyFallback)
            {
                OnLegacyFallback = true;
                Debug.LogWarning("[DreamParkAPI] " + ApiHostBaseUrl + " unreachable (" + (why ?? "transport failure") +
                                 "); using " + LegacyBaseUrl + " for the rest of this session.");
            }
            return LegacyBaseUrl + url.Substring(ApiHostBaseUrl.Length);
        }

        /// A URL built on the API host before this session fell back, moved to dreampark.app. Anything else unchanged.
        public static string OnCurrentHost(string url) =>
            OnLegacyFallback && IsOnApiHost(url) ? LegacyBaseUrl + url.Substring(ApiHostBaseUrl.Length) : url;

        /// The website base for paths api.qrcd.is does not serve (a crash report, a share image): dreampark.app while
        /// baseUrl is the API host, else baseUrl itself (a test server serves everything).
        public static string WebBaseUrl =>
            string.Equals(baseUrl, ApiHostBaseUrl, StringComparison.OrdinalIgnoreCase) ? LegacyBaseUrl : baseUrl;

        /// The base for a site-relative path: baseUrl for an API path (/app /api /auth /internal /storage — what the
        /// API host serves), else <see cref="WebBaseUrl"/>.
        public static string BaseUrlForPath(string path) => IsApiPath(path) ? baseUrl : WebBaseUrl;

        public static bool IsApiPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (string p in ApiPathRoots)
                if (path.StartsWith(p, StringComparison.Ordinal) &&
                    (path.Length == p.Length || path[p.Length] == '/' || path[p.Length] == '?')) return true;
            return false;
        }

        static readonly string[] ApiPathRoots = { "/app", "/api", "/auth", "/internal", "/storage" };

        static bool IsOnApiHost(string url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith(ApiHostBaseUrl, StringComparison.OrdinalIgnoreCase) &&
            (url.Length == ApiHostBaseUrl.Length || url[ApiHostBaseUrl.Length] == '/' || url[ApiHostBaseUrl.Length] == '?');

        public static void POST(string endpoint, string authToken, object body, Action<bool, APIResponse> callback)
        {
            var url = baseUrl + endpoint;
            byte[] bodyRaw = null;
            if (body is JSONObject) {
                bodyRaw = System.Text.Encoding.UTF8.GetBytes((body ?? new JSONObject()).ToString());
            } else if (body is byte[]) {
                bodyRaw = body as byte[];
            }else if (body is List<KeyValuePair<string, UploadContentData>>) {
                Debug.Log("Posting multipart: " + body);
                #if UNITY_EDITOR
                EditorCoroutineUtility.StartCoroutineOwnerless(PostMultipart(url, authToken, body as List<KeyValuePair<string, UploadContentData>>, callback));
                #else
                CoroutineRunner.Run(PostMultipart(url, authToken, body as List<KeyValuePair<string, UploadContentData>>, callback));
                #endif
                return;
            }
            Debug.Log("Posting request: " + url);
            #if UNITY_EDITOR
            EditorCoroutineUtility.StartCoroutineOwnerless(PostRequest(url, authToken, bodyRaw, callback));
            #else
            CoroutineRunner.Run(PostRequest(url, authToken, bodyRaw, callback));
            #endif
        }
        public static void PUT(string endpoint, string authToken, byte[] data, string contentType, Action<bool, string> callback)
        {
            PUT(endpoint, authToken, data, contentType, null, callback);
        }

        public static void PUT(string endpoint, string authToken, byte[] data, string contentType, Action<float> progressCallback, Action<bool, string> callback)
        {
            var url = endpoint.StartsWith("http") ? endpoint : baseUrl + endpoint;
            #if UNITY_EDITOR
            EditorCoroutineUtility.StartCoroutineOwnerless(PutRequest(url, authToken, data, contentType, progressCallback, callback));
            #else
            CoroutineRunner.Run(PutRequest(url, authToken, data, contentType, progressCallback, callback));
            #endif
        }

        public static void GET(string endpoint, string authToken, Action<bool, APIResponse> callback)
        {
            GET(endpoint, authToken, null, callback);
        }

        /// `ifNoneMatch` — a previously stored ETag, offered back verbatim.
        /// A 304 comes back as success with an empty body and statusCode 304:
        /// the caller keeps whatever it cached.
        public static void GET(string endpoint, string authToken, string ifNoneMatch, Action<bool, APIResponse> callback)
        {
            var url = baseUrl + endpoint;
            #if UNITY_EDITOR
            EditorCoroutineUtility.StartCoroutineOwnerless(GetRequest(url, authToken, ifNoneMatch, callback));
            #else
            CoroutineRunner.Run(GetRequest(url, authToken, ifNoneMatch, callback));
            #endif
        }
        // UnityWebRequest's default timeout is 0 — WAIT FOREVER. A server
        // that accepts the connection but never responds (e.g. a crashed
        // route handler) used to hang the request eternally, and with it any
        // UI waiting on the callback (the stuck park-save overlay). Every
        // request gets a hard ceiling; the callback then fires with a
        // timeout error like any other failure.
        private const int RequestTimeoutSeconds = 30;
        private const int UploadTimeoutSeconds = 180; // large multipart/PUT payloads

        /// Sends one request and, on a transport failure to the API host, once more on dreampark.app (qr-hosts.md §5).
        /// `make` builds the request for a URL (a UnityWebRequest cannot be sent twice); `done` reads the finished
        /// request before it is disposed. Auth is always a header, so it does not care which host answers.
        private static IEnumerator SendWithFallback(string url, Func<string, UnityWebRequest> make, Action<float> progress,
            Action<UnityWebRequest> done)
        {
            // A URL built on the API host before this session fell back (an absolute endpoint, a queued retry) goes straight
            // to dreampark.app: otherwise it would wait out a whole timeout on a host already known to be unreachable.
            string next = OnCurrentHost(url);
            for (int attempt = 0; ; attempt++)
            {
                using (UnityWebRequest req = make(next))
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        progress?.Invoke(req.uploadProgress);
                        yield return null;
                    }
                    // The session switches on any transport failure (FallbackUrlAfter); the request itself is retried
                    // only when that is safe for its method (MayRetryOnFallback).
                    string retry = attempt == 0 ? FallbackUrlAfter(req) : null;
                    if (retry != null && !MayRetryOnFallback(req.method, req.error))
                    {
                        Debug.LogWarning("[DreamParkAPI] " + req.method + " not retried on " + LegacyBaseUrl +
                                         " (" + req.error + ": it may have reached the server)");
                        retry = null;
                    }
                    if (retry == null)
                    {
                        done(req);
                        yield break;
                    }
                    next = retry;
                }
            }
        }

        private static IEnumerator PostRequest(string url, string authToken, byte[] bodyRaw, Action<bool, APIResponse> callback)
        {
            return SendWithFallback(url, u =>
            {
                var req = new UnityWebRequest(u, UnityWebRequest.kHttpVerbPOST);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;

                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(authToken))
                    req.SetRequestHeader("Authorization", authToken);
                return req;
            }, null, req =>
            {
                var response = BuildResponse(req);
                callback?.Invoke(response.success, response);
            });
        }

        /// Park reads are authorized per DEVICE (the headset report), which only knows
        /// who logged in by QR pairing — not who is signed into the iOS app. A new
        /// account's own park was a 403 on the phone that made it. Device-key reads of
        /// /app/park/* therefore also carry the signed-in session in
        /// X-DreamPark-User-Session (server: app.park.routes userSessionUid), which the
        /// server honours only beside a valid device key and only for the park team.
        /// Core-only; our own host only; a no-op when nobody is signed in.
        internal static void AttachUserSession(UnityWebRequest req, string url, string authToken)
        {
#if DREAMPARKCORE
            if (string.IsNullOrEmpty(authToken) || !authToken.StartsWith("ApiKey ")) return;
            if (string.IsNullOrEmpty(url) || !url.StartsWith(baseUrl + "/app/park/")) return;
            string session = AuthAPI.HumanSessionForDeviceReads;
            if (!string.IsNullOrEmpty(session))
                req.SetRequestHeader("X-DreamPark-User-Session", session);
#endif
        }

        private static IEnumerator GetRequest(string url, string authToken, string ifNoneMatch, Action<bool, APIResponse> callback)
        {
            return SendWithFallback(url, u =>
            {
                var req = new UnityWebRequest(u, UnityWebRequest.kHttpVerbGET);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
                if (!string.IsNullOrEmpty(authToken))
                    req.SetRequestHeader("Authorization", authToken);
                AttachUserSession(req, u, authToken);
                if (!string.IsNullOrEmpty(ifNoneMatch))
                    req.SetRequestHeader("If-None-Match", ifNoneMatch);
                return req;
            }, null, req =>
            {
                var response = BuildResponse(req);
                callback?.Invoke(response.success, response);
            });
        }

        private static IEnumerator PostMultipart(string url, string authToken, List<KeyValuePair<string, UploadContentData>> files, Action<bool, APIResponse> callback)
        {
            WWWForm form = new WWWForm();

            // Add multiple files
            foreach (var kvp in files)
            {
                form.AddBinaryData(kvp.Key, kvp.Value.data, kvp.Value.fileName, kvp.Value.mimeType);
            }

            return SendWithFallback(url, u =>
            {
                UnityWebRequest req = UnityWebRequest.Post(u, form);
                req.timeout = UploadTimeoutSeconds;
                if (!string.IsNullOrEmpty(authToken))
                    req.SetRequestHeader("Authorization", authToken);
                return req;
            }, null, req =>
            {
                var response = BuildResponse(req);
                callback?.Invoke(response.success, response);
            });
        }

        private static IEnumerator PutRequest(string url, string authToken, byte[] data, string contentType, Action<float> progressCallback, Action<bool, string> callback)
        {
            return SendWithFallback(url, u =>
            {
                var put = new UnityWebRequest(u, "PUT");
                put.uploadHandler = new UploadHandlerRaw(data);
                put.downloadHandler = new DownloadHandlerBuffer();
                put.timeout = UploadTimeoutSeconds;
                put.SetRequestHeader("Content-Type", contentType);
                // Only when a token is actually supplied: every presigned-URL
                // caller (screenshots, content bundles) passes "" — a GCS
                // signed URL REJECTS a request that also carries Authorization,
                // so this must never send an empty or unasked-for header.
                if (!string.IsNullOrEmpty(authToken))
                    put.SetRequestHeader("Authorization", authToken);
                return put;
            }, progressCallback, req =>
            {
                progressCallback?.Invoke(1f);

                bool success = req.result == UnityWebRequest.Result.Success || req.responseCode == 200;
                if (success)
                    Debug.Log("✅ Upload completed successfully.");
                else
                    Debug.LogError($"❌ Upload failed: {req.responseCode} - {req.error}");

                callback?.Invoke(success, req.downloadHandler.text);
            });
        }

        // ---- Shared Response Builder ----
        private static APIResponse BuildResponse(UnityWebRequest req)
        {
            bool success = !(req.result == UnityWebRequest.Result.ConnectionError ||
                            req.result == UnityWebRequest.Result.ProtocolError);

            string contentType = req.GetResponseHeader("Content-Type") ?? "";
            bool isTextPayload = string.IsNullOrEmpty(contentType)
                                 || contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0
                                 || contentType.IndexOf("text/", StringComparison.OrdinalIgnoreCase) >= 0;

            string rawText = "";
            if (req.downloadHandler != null && isTextPayload)
            {
                rawText = req.downloadHandler.text ?? "";
            }
            byte[] data = req.downloadHandler != null ? req.downloadHandler.data : null;

            var response = new APIResponse(
                success,
                req.responseCode,
                success ? null : req.error,
                rawText,
                data
            );
            // UnityWebRequest.error only says "400 Bad Request". The API's
            // JSON error names the rejected field or artifact, which the
            // uploader needs to show instead of losing it here.
            if (!success)
            {
                string serverError = response.json?.GetField("error")?.stringValue;
                if (!string.IsNullOrWhiteSpace(serverError)) response.error = serverError;
            }
            response.etag = req.GetResponseHeader("ETag");
            return response;
        }
    }
}
