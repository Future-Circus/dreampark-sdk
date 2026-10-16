#if UNITY_EDITOR
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.Playtest
{
    public class PlaytestApiException : Exception
    {
        public readonly long status;
        public PlaytestApiException(long status, string message) : base(message) { this.status = status; }
    }

    /// <summary>
    /// Minimal Claude Messages API client over UnityWebRequest.
    ///
    /// Raw HTTP rather than the official C# SDK on purpose: that SDK targets
    /// modern .NET and pulls in dependencies Unity's Mono / .NET Standard 2.1
    /// toolchain can't consume without NuGet tooling this project doesn't use.
    /// The request/response shapes here are the documented wire format.
    /// </summary>
    public class PlaytestClaudeClient
    {
        const string DefaultBaseUrl = "https://api.anthropic.com";
        const string ApiVersion = "2023-06-01";
        // Opus 5's safety classifiers can decline a benign request; with the
        // server-side fallback opted in, the API re-serves it on the model
        // Anthropic recommends for that category instead of returning a refusal.
        const string FallbackBeta = "server-side-fallback-2026-07-01";

        readonly string _apiKey;
        readonly string _baseUrl;
        UnityWebRequest _inFlight;

        public PlaytestClaudeClient(string apiKey, string baseUrl)
        {
            _apiKey = apiKey;
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
        }

        public static bool UsesFallbacks(string model) => model == "claude-opus-5";

        public void Abort()
        {
            try { _inFlight?.Abort(); } catch { /* already disposed */ }
        }

        public async Task<JObject> CreateMessage(JObject body, CancellationToken cancel)
        {
            string model = body.Value<string>("model");
            if (UsesFallbacks(model) && body["fallbacks"] == null) body["fallbacks"] = "default";
            byte[] payload = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));

            int attempt = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                using (var req = new UnityWebRequest(_baseUrl + "/v1/messages", UnityWebRequest.kHttpVerbPOST))
                {
                    req.uploadHandler = new UploadHandlerRaw(payload) { contentType = "application/json" };
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.timeout = 600;
                    req.SetRequestHeader("content-type", "application/json");
                    if (!string.IsNullOrEmpty(_apiKey)) req.SetRequestHeader("x-api-key", _apiKey);
                    req.SetRequestHeader("anthropic-version", ApiVersion);
                    if (UsesFallbacks(model)) req.SetRequestHeader("anthropic-beta", FallbackBeta);

                    _inFlight = req;
                    await Send(req);
                    _inFlight = null;
                    cancel.ThrowIfCancellationRequested();

                    long status = req.responseCode;
                    string text = req.downloadHandler?.text ?? "";
                    if (req.result == UnityWebRequest.Result.Success && status == 200)
                        return JObject.Parse(text);

                    bool retryable = status == 429 || status == 529 || status >= 500 || status == 0;
                    if (retryable && attempt < 4)
                    {
                        attempt++;
                        float wait = Mathf.Pow(2f, attempt);
                        string retryAfter = req.GetResponseHeader("retry-after");
                        if (float.TryParse(retryAfter, out float ra)) wait = Mathf.Clamp(ra, 1f, 60f);
                        Debug.LogWarning($"[Playtest] Claude API {status}; retrying in {wait:0}s");
                        await Task.Delay(TimeSpan.FromSeconds(wait), cancel);
                        continue;
                    }
                    throw new PlaytestApiException(status, DescribeError(status, text, req.error));
                }
            }
        }

        static Task Send(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            var op = req.SendWebRequest();
            if (op.isDone) tcs.TrySetResult(true);
            else op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }

        static string DescribeError(long status, string body, string transportError)
        {
            string detail = transportError;
            try
            {
                var err = JObject.Parse(body)["error"];
                if (err != null) detail = $"{err.Value<string>("type")}: {err.Value<string>("message")}";
            }
            catch { /* non-JSON body */ }
            if (status == 401) return "The API key was rejected (401). Check it in the AI Playtester window.";
            return $"Claude API error {status}: {detail}";
        }
    }
}
#endif
