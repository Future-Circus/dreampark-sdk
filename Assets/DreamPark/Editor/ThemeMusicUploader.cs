#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Defective.JSON;
using DreamPark.API;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ThemeMusic
{
    // Pushes the Content Uploader's "Theme music" clip to the title's App Page.
    // The app plays it when the title's page opens or the title is picked in
    // Play, through the load. Contract: DreamPark-Web
    // docs/contracts/title-theme.md.
    //
    // ── The endpoints (owner-authorized, like /api/content/:id/logo) ─────────
    //
    //   POST /api/content/:id/theme/upload-url  { contentType, fileName, byteSize }
    //        → { uploadId, uploadUrl, contentType }
    //   PUT  <uploadUrl>                         raw bytes, Content-Type as answered,
    //                                            NO Authorization (GCS refuses it)
    //   POST /api/content/:id/theme/commit       { uploadId, fileName }
    //        → { theme: { url, durationSec, ... }, trimmed }
    //   GET  /api/content/:id/theme              → { theme, stored }
    //
    // The bytes go straight to Cloud Storage on a signed URL — a WAV is tens of
    // MB, which through the web server would time out on a slow uplink. The
    // commit is synchronous: the server measures and normalizes the loudness
    // (-16 LUFS), encodes AAC, and answers when the theme is live. So we send
    // the ORIGINAL file from disk (no re-encode here; Unity has no AAC encoder
    // and the server's is the one that sets the loudness everyone shares).
    //
    // ── Silent pushes skip an unchanged file ─────────────────────────────────
    // Every Upload Release would otherwise re-encode the same track and, worse,
    // overwrite a theme the developer replaced on the dashboard since. The
    // last pushed file's MD5 is remembered per target content id; the explicit
    // "Push Theme to App Page" button always pushes.
    public static class ThemeMusicUploader
    {
        // Mirrors lib/titleThemeCore MAX_SOURCE_BYTES.
        public const long MaxSourceBytes = 40L * 1024 * 1024;
        public const int MaxSeconds = 180;

        private const string LastPushedHashPrefix = "DreamPark.ContentUploader.ThemeHash.";

        private static readonly Dictionary<string, string> MimeByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".mp3", "audio/mpeg" },
                { ".m4a", "audio/mp4" },
                { ".aac", "audio/mp4" },
                { ".wav", "audio/wav" },
                { ".aif", "audio/aiff" },
                { ".aiff", "audio/aiff" },
                { ".ogg", "audio/ogg" },
                { ".flac", "audio/flac" },
            };

        public class Result
        {
            public bool ok;
            public bool skipped;            // unchanged since the last push
            public string error;
            public double durationSec;
            public bool trimmed;
            public string url;
        }

        public class ServerStatus
        {
            public bool ok;
            public bool hasTheme;
            public double durationSec;
            public string sourceName;
            public long updatedAtMs;
            public bool hasLoop;
            public string error;
        }

        /// The clip's source file, as long as it is a format the server takes
        /// and within the size cap. Returns null with a reason otherwise.
        public static string SourcePath(AudioClip clip, out string mimeType, out string error)
        {
            mimeType = null;
            error = null;
            if (clip == null) { error = "No theme music selected."; return null; }
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "The theme music clip has no file in the project.";
                return null;
            }
            string ext = Path.GetExtension(path);
            if (!MimeByExtension.TryGetValue(ext ?? "", out mimeType))
            {
                error = $"{Path.GetFileName(path)} is a {ext} file. Theme music must be MP3, M4A, WAV, AIFF, OGG or FLAC.";
                return null;
            }
            long bytes = new FileInfo(path).Length;
            if (bytes > MaxSourceBytes)
            {
                error = $"{Path.GetFileName(path)} is {bytes / (1024 * 1024)} MB; the limit is "
                        + $"{MaxSourceBytes / (1024 * 1024)} MB. Export it as MP3 or M4A.";
                return null;
            }
            return path;
        }

        /// A one-line warning for the panel, or null when the clip is fine.
        public static string Warning(AudioClip clip)
        {
            if (clip == null) return null;
            SourcePath(clip, out _, out string error);
            if (error != null) return error;
            if (clip.length > MaxSeconds + 0.5f)
                return $"Only the first {MaxSeconds / 60} minutes will be used (with a short fade-out).";
            return null;
        }

        /// Push from the upload flow: never blocks, never fails the upload, and
        /// skips a file identical to the last one pushed to this content id.
        public static void PushSilently(string contentId, AudioClip clip, Action<Result> onDone = null)
        {
            try
            {
                if (string.IsNullOrEmpty(contentId) || clip == null) return;
                EditorCoroutineUtility.StartCoroutineOwnerless(PushRoutine(contentId, clip, false, onDone));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Theme] push skipped: " + e.Message);
                onDone?.Invoke(new Result { ok = false, error = e.Message });
            }
        }

        /// The "Push Theme to App Page" button: always pushes, reports in a dialog.
        public static void Push(string contentId, AudioClip clip, Action<Result> onDone = null)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(PushRoutine(contentId, clip, true, onDone));
        }

        private static IEnumerator PushRoutine(string contentId, AudioClip clip, bool interactive, Action<Result> onDone)
        {
            var result = new Result();
            string auth = AuthAPI.GetUserAuthFor(contentId);
            string path = SourcePath(clip, out string mimeType, out string sourceError);

            if (string.IsNullOrEmpty(auth) || !AuthAPI.isLoggedIn) sourceError = "Not signed in to DreamPark.";
            if (sourceError != null)
            {
                result.error = sourceError;
                Finish(result, interactive, onDone);
                yield break;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e)
            {
                result.error = "Could not read " + path + ": " + e.Message;
                Finish(result, interactive, onDone);
                yield break;
            }
            string fileName = Path.GetFileName(path);
            string hash = Md5(bytes);
            string hashKey = LastPushedHashPrefix + contentId;
            if (!interactive && EditorPrefs.GetString(hashKey, "") == hash)
            {
                result.ok = true;
                result.skipped = true;
                onDone?.Invoke(result);
                yield break;
            }

            string basePath = "/api/content/" + Uri.EscapeDataString(contentId) + "/theme";

            // 1. Signed URL.
            var mint = new JSONObject();
            mint.AddField("contentType", mimeType);
            mint.AddField("fileName", fileName);
            mint.AddField("byteSize", (long)bytes.Length);
            DreamParkAPI.APIResponse minted = null;
            DreamParkAPI.POST(basePath + "/upload-url", auth, mint, (s, r) => minted = r ?? new DreamParkAPI.APIResponse(false, 0, "no response"));
            if (interactive) EditorUtility.DisplayProgressBar("Theme music", "Starting upload…", 0.05f);
            while (minted == null) yield return null;
            string uploadId = minted.json?.GetField("uploadId")?.stringValue;
            string uploadUrl = minted.json?.GetField("uploadUrl")?.stringValue;
            string putType = minted.json?.GetField("contentType")?.stringValue ?? mimeType;
            if (!minted.success || string.IsNullOrEmpty(uploadId) || string.IsNullOrEmpty(uploadUrl))
            {
                result.error = Describe(minted, "could not start the upload");
                Finish(result, interactive, onDone);
                yield break;
            }

            // 2. Bytes straight to Cloud Storage. Empty auth: a signed URL
            //    refuses a request that also carries Authorization.
            bool? putOk = null;
            string putBody = null;
            DreamParkAPI.PUT(uploadUrl, "", bytes, putType,
                p => { if (interactive) EditorUtility.DisplayProgressBar("Theme music", $"Uploading {fileName}…", 0.05f + 0.6f * p); },
                (ok, body) => { putBody = body; putOk = ok; });
            while (putOk == null) yield return null;
            if (putOk != true)
            {
                result.error = "the upload to storage failed" + (string.IsNullOrEmpty(putBody) ? "" : ": " + Truncate(putBody, 200));
                Finish(result, interactive, onDone);
                yield break;
            }

            // 3. Commit: the server normalizes, encodes and publishes before it answers.
            if (interactive) EditorUtility.DisplayProgressBar("Theme music", "Evening out the volume and encoding…", 0.75f);
            var commit = new JSONObject();
            commit.AddField("uploadId", uploadId);
            commit.AddField("fileName", fileName);
            DreamParkAPI.APIResponse committed = null;
            DreamParkAPI.POST(basePath + "/commit", auth, commit, (s, r) => committed = r ?? new DreamParkAPI.APIResponse(false, 0, "no response"));
            while (committed == null) yield return null;

            JSONObject theme = committed.json?.GetField("theme");
            bool success = committed.success && committed.json != null
                           && committed.json.HasField("success") && committed.json["success"].boolValue
                           && theme != null && !theme.isNull;
            if (!success)
            {
                result.error = Describe(committed, "the server did not confirm the theme");
                Finish(result, interactive, onDone);
                yield break;
            }

            result.ok = true;
            result.url = theme.GetField("url")?.stringValue;
            result.durationSec = Number(theme.GetField("durationSec"));
            result.trimmed = committed.json.GetField("trimmed")?.boolValue ?? false;
            EditorPrefs.SetString(hashKey, hash);
            Debug.Log($"[Theme] {fileName} is live on the App Page for {contentId} ({FormatDuration(result.durationSec)}"
                      + (result.trimmed ? ", trimmed" : "") + ").");
            Finish(result, interactive, onDone);
        }

        private static void Finish(Result result, bool interactive, Action<Result> onDone)
        {
            if (interactive)
            {
                EditorUtility.ClearProgressBar();
                if (result.ok)
                {
                    EditorUtility.DisplayDialog("Theme music pushed",
                        $"Live on your App Page ({FormatDuration(result.durationSec)}). The DreamPark app plays it "
                        + "when players open your game's page or pick it in Play."
                        + (result.trimmed ? $"\n\nThe track was longer than {MaxSeconds / 60} minutes, so only the first {MaxSeconds / 60} minutes are used." : ""),
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Theme music not pushed", result.error ?? "Unknown error.", "OK");
                }
            }
            else if (!result.ok && !string.IsNullOrEmpty(result.error))
            {
                Debug.LogWarning("[Theme] push failed: " + result.error);
            }
            onDone?.Invoke(result);
        }

        /// What the App Page has now (the dashboard may have replaced it).
        public static void FetchStatus(string contentId, Action<ServerStatus> onDone)
        {
            string auth = AuthAPI.GetUserAuthFor(contentId);
            if (string.IsNullOrEmpty(contentId) || string.IsNullOrEmpty(auth) || !AuthAPI.isLoggedIn)
            {
                onDone?.Invoke(new ServerStatus { ok = false, error = "Not signed in." });
                return;
            }
            DreamParkAPI.GET("/api/content/" + Uri.EscapeDataString(contentId) + "/theme", auth, (s, r) =>
            {
                var status = new ServerStatus();
                if (!s || r?.json == null)
                {
                    status.error = r?.statusCode == 404 ? null : Describe(r, "could not read the App Page");
                    status.ok = r?.statusCode == 404;   // no content doc yet = no theme yet
                    onDone?.Invoke(status);
                    return;
                }
                status.ok = true;
                JSONObject theme = r.json.GetField("theme");
                if (theme != null && !theme.isNull)
                {
                    status.hasTheme = true;
                    status.durationSec = Number(theme.GetField("durationSec"));
                    status.updatedAtMs = (long)Number(theme.GetField("updatedAtMs"));
                    status.hasLoop = theme.HasField("loopStartSec");
                    JSONObject stored = r.json.GetField("stored");
                    status.sourceName = stored != null && !stored.isNull ? stored.GetField("sourceName")?.stringValue : null;
                }
                onDone?.Invoke(status);
            });
        }

        // ── helpers ────────────────────────────────────────────────────────

        public static string FormatDuration(double seconds)
        {
            if (!(seconds > 0)) return "0:00";
            int s = (int)Math.Round(seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        private static double Number(JSONObject n)
        {
            if (n == null || n.isNull) return 0;
            return n.isInteger ? n.longValue : n.doubleValue;
        }

        private static string Describe(DreamParkAPI.APIResponse r, string fallback)
        {
            if (r == null) return fallback;
            string err = r.json?.GetField("error")?.stringValue;
            if (!string.IsNullOrEmpty(err)) return err;
            if (r.statusCode == 403) return "not an owner of this content (403).";
            if (r.statusCode == 401) return "session expired — sign out and back in (DreamPark ▸ Sign In).";
            if (!string.IsNullOrEmpty(r.error)) return r.error;
            return r.statusCode > 0 ? $"{fallback} (HTTP {r.statusCode})" : fallback;
        }

        private static string Md5(byte[] bytes)
        {
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string Truncate(string s, int n) => s == null || s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // Editor preview through Unity's own (internal) AudioUtil — the same player
    // the AudioClip inspector uses, so nothing is added to the scene. Looked up
    // by name because the class is internal; when a Unity version renames it,
    // the button simply does nothing rather than throwing.
    public static class ThemePreview
    {
        private static Type audioUtil;
        private static AudioClip playing;

        private static Type AudioUtil
        {
            get
            {
                if (audioUtil == null)
                    audioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
                return audioUtil;
            }
        }

        private static object Call(string name, params object[] args)
        {
            var t = AudioUtil;
            if (t == null) return null;
            var method = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length >= args.Length
                                     && m.GetParameters().Skip(args.Length).All(p => p.IsOptional));
            if (method == null) return null;
            var parameters = method.GetParameters();
            var full = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                full[i] = i < args.Length ? args[i] : parameters[i].DefaultValue;
            try { return method.Invoke(null, full); }
            catch (Exception e) { Debug.LogWarning($"[Theme] preview {name} failed: {e.InnerException?.Message ?? e.Message}"); return null; }
        }

        public static bool IsPlaying(AudioClip clip)
        {
            if (clip == null || playing != clip) return false;
            object r = Call("IsPreviewClipPlaying");
            bool on = r is bool b && b;
            if (!on) playing = null;
            return on;
        }

        public static void Play(AudioClip clip)
        {
            Stop();
            if (clip == null) return;
            Call("PlayPreviewClip", clip, 0, false);
            playing = clip;
        }

        public static void Stop()
        {
            Call("StopAllPreviewClips");
            playing = null;
        }
    }
}
#endif
