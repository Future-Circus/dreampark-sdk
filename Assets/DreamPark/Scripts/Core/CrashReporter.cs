using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DreamPark.API;

namespace DreamPark
{
    public static class CrashReporter
    {
        private const int MaxReportsPerMinute = 10;
        private const int MaxQueuedReports = 20;

        private static int _reportCount;
        private static long _windowStartTicks;
        private static bool _initialized;
        private static string _queuePath;

        // Cached at init (main thread) so background threads can read safely
        private static string _platform;
        private static string _appVersion;
        private static string _osVersion;
        private static string _deviceModel;

        // Thread-safe queue for reports from background threads
        private static readonly Queue<string> _pendingReports = new Queue<string>();
        private static readonly object _pendingLock = new object();
        private static readonly object _rateLock = new object();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            // Cache main-thread-only values
            _platform = Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "quest";
            _appVersion = Application.version;
            _osVersion = SystemInfo.operatingSystem;
            _deviceModel = SystemInfo.deviceModel;
            _queuePath = Path.Combine(Application.persistentDataPath, "crash_queue.json");
            _windowStartTicks = DateTime.UtcNow.Ticks;

            Application.logMessageReceivedThreaded += OnLogReceived;

            // Start a coroutine to drain pending reports and flush offline queue
            FlushQueue();
            StartDrainLoop();
        }

        private static void OnLogReceived(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert)
                return;

            // Thread-safe rate limiting using DateTime (works on any thread)
            lock (_rateLock)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (nowTicks - _windowStartTicks > TimeSpan.TicksPerMinute)
                {
                    _reportCount = 0;
                    _windowStartTicks = nowTicks;
                }

                if (_reportCount >= MaxReportsPerMinute)
                    return;

                _reportCount++;
            }

            string level = type == LogType.Exception ? "fatal" : "error";
            string userId = null;
            string sceneName = "";
            try { userId = PlayerPrefs.GetString("userId", null); } catch { }
            try { sceneName = SceneManager.GetActiveScene().name; } catch { }

            string json = BuildJson(level, message, stackTrace, _platform, userId, sceneName);

            // Queue for main-thread dispatch (CoroutineRunner requires main thread)
            lock (_pendingLock)
            {
                _pendingReports.Enqueue(json);
            }
        }

        /// <summary>
        /// Manually record an error with additional context (call from catch blocks).
        /// </summary>
        public static void RecordError(Exception e, string context)
        {
            if (e == null) return;
            OnLogReceived($"[{context}] {e.Message}", e.StackTrace ?? "", LogType.Error);
        }

        private static void StartDrainLoop()
        {
#if UNITY_EDITOR
            Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(DrainLoop());
#else
            CoroutineRunner.Run(DrainLoop());
#endif
        }

        private static IEnumerator DrainLoop()
        {
            // Wait one frame for CoroutineRunner to be ready
            yield return null;

            while (true)
            {
                string json = null;
                lock (_pendingLock)
                {
                    if (_pendingReports.Count > 0)
                        json = _pendingReports.Dequeue();
                }

                if (json != null)
                {
                    Debug.Log($"[CrashReporter] Sending crash report");
                    yield return PostReport(json);
                }
                else
                {
                    // Check every 0.5s when idle
                    yield return new WaitForSeconds(0.5f);
                }
            }
        }

        private static string BuildJson(string level, string message, string stackTrace,
            string platform, string userId, string sceneName)
        {
            // Manual JSON build — safe for background threads (no Unity API calls)
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendField(sb, "level", level); sb.Append(',');
            AppendField(sb, "message", message); sb.Append(',');
            AppendField(sb, "stackTrace", stackTrace); sb.Append(',');
            AppendField(sb, "platform", platform); sb.Append(',');
            AppendField(sb, "timestamp", DateTime.UtcNow.ToString("o")); sb.Append(',');
            AppendField(sb, "appVersion", _appVersion); sb.Append(',');
            AppendField(sb, "osVersion", _osVersion); sb.Append(',');
            AppendField(sb, "deviceModel", _deviceModel); sb.Append(',');
            if (!string.IsNullOrEmpty(userId))
            {
                AppendField(sb, "userId", userId); sb.Append(',');
            }
            sb.Append("\"context\":{");
            AppendField(sb, "scene", sceneName);
            sb.Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"");
            if (value != null)
            {
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default: sb.Append(c); break;
                    }
                }
            }
            sb.Append('"');
        }

        private static IEnumerator PostReport(string json)
        {
            string url = DreamParkAPI.devBaseUrl + "/crashes/report";
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.ConnectionError ||
                    req.result == UnityWebRequest.Result.ProtocolError)
                {
                    EnqueueReport(json);
                }
            }
        }

        #region Offline Queue

        private static void EnqueueReport(string jsonReport)
        {
            try
            {
                List<string> queue = LoadQueue();
                if (queue.Count >= MaxQueuedReports)
                    queue.RemoveAt(0);
                queue.Add(jsonReport);
                SaveQueue(queue);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CrashReporter] Failed to queue report: {e.Message}");
            }
        }

        private static void FlushQueue()
        {
            try
            {
                List<string> queue = LoadQueue();
                if (queue.Count == 0) return;

                var sb = new StringBuilder();
                sb.Append("{\"reports\":[");
                for (int i = 0; i < queue.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(queue[i]);
                }
                sb.Append("]}");

                string batchJson = sb.ToString();
                File.Delete(_queuePath);

#if UNITY_EDITOR
                Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(PostBatch(batchJson));
#else
                CoroutineRunner.Run(PostBatch(batchJson));
#endif
                Debug.Log($"[CrashReporter] Flushing {queue.Count} queued crash reports");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CrashReporter] Failed to flush queue: {e.Message}");
            }
        }

        private static IEnumerator PostBatch(string json)
        {
            string url = DreamParkAPI.devBaseUrl + "/crashes/report/batch";
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 15;

                yield return req.SendWebRequest();
            }
        }

        private static List<string> LoadQueue()
        {
            var queue = new List<string>();
            if (!File.Exists(_queuePath)) return queue;

            try
            {
                string raw = File.ReadAllText(_queuePath);
                if (string.IsNullOrWhiteSpace(raw)) return queue;

                string[] lines = raw.Split('\n');
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 2)
                        queue.Add(trimmed);
                }
            }
            catch { }

            return queue;
        }

        private static void SaveQueue(List<string> queue)
        {
            File.WriteAllText(_queuePath, string.Join("\n", queue));
        }

        #endregion
    }
}
