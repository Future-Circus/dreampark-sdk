#if UNITY_EDITOR
using System;
using UnityEditor;

namespace DreamPark.Playtest
{
    /// <summary>
    /// Per-machine AI Playtester preferences (EditorPrefs). The API key is stored
    /// here only when the creator types it into the window; ANTHROPIC_API_KEY in the
    /// Editor's environment always wins, and the key is never written to the
    /// project, a report, or a log.
    /// </summary>
    public static class PlaytestSettings
    {
        const string Prefix = "DreamPark.Playtest.";

        public static readonly string[] Models = { "claude-opus-5", "claude-sonnet-5" };
        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh" };

        public static string StoredApiKey
        {
            get => EditorPrefs.GetString(Prefix + "ApiKey", "");
            set => EditorPrefs.SetString(Prefix + "ApiKey", value ?? "");
        }

        public static string EnvApiKey => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";

        public static string ApiKey => !string.IsNullOrEmpty(EnvApiKey) ? EnvApiKey : StoredApiKey;

        /// Lets a studio route calls through its own proxy. Empty = api.anthropic.com.
        public static string BaseUrl
        {
            get => EditorPrefs.GetString(Prefix + "BaseUrl", "");
            set => EditorPrefs.SetString(Prefix + "BaseUrl", value ?? "");
        }

        public static string Model
        {
            get => EditorPrefs.GetString(Prefix + "Model", Models[0]);
            set => EditorPrefs.SetString(Prefix + "Model", value);
        }

        public static string Effort
        {
            get => EditorPrefs.GetString(Prefix + "Effort", "high");
            set => EditorPrefs.SetString(Prefix + "Effort", value);
        }

        public static int MaxSteps
        {
            get => EditorPrefs.GetInt(Prefix + "MaxSteps", 60);
            set => EditorPrefs.SetInt(Prefix + "MaxSteps", Math.Max(5, value));
        }

        public static bool FreezeTime
        {
            get => EditorPrefs.GetBool(Prefix + "FreezeTime", true);
            set => EditorPrefs.SetBool(Prefix + "FreezeTime", value);
        }

        public static bool AutoScreenshot
        {
            get => EditorPrefs.GetBool(Prefix + "AutoScreenshot", true);
            set => EditorPrefs.SetBool(Prefix + "AutoScreenshot", value);
        }

        public static bool BodyProbe
        {
            get => EditorPrefs.GetBool(Prefix + "BodyProbe", true);
            set => EditorPrefs.SetBool(Prefix + "BodyProbe", value);
        }

        public static bool AllowFixes
        {
            get => EditorPrefs.GetBool(Prefix + "AllowFixes", false);
            set => EditorPrefs.SetBool(Prefix + "AllowFixes", value);
        }

        public static string Goal
        {
            get => EditorPrefs.GetString(Prefix + "Goal", "");
            set => EditorPrefs.SetString(Prefix + "Goal", value ?? "");
        }

        public static string ContentId
        {
            get => EditorPrefs.GetString(Prefix + "ContentId", "");
            set => EditorPrefs.SetString(Prefix + "ContentId", value ?? "");
        }
    }
}
#endif
