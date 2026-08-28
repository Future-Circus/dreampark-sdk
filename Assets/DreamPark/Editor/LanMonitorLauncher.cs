#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DreamPark
{
    /// <summary>
    /// Unity Editor menu for the LAN Monitor (Tools/LanMonitor/) — a read-only
    /// observer for headset-to-headset multiplayer traffic.
    ///
    ///   DreamPark → Multiplayer → Start LAN Monitor
    ///     1. If it's already running, just open the panel.
    ///     2. Else launch it (`dotnet run`, or a prebuilt binary from dist/).
    ///     3. Poll the panel port; open the browser when it answers.
    ///
    /// The monitor JOINS a session as an ordinary peer and never transmits.
    /// It does not host a relay and does not broadcast a beacon, so starting it
    /// cannot preempt a peer election or otherwise disturb the session you are
    /// trying to watch. That is the whole point of it being a separate tool
    /// from Tools/DreamBoxServer rather than a mode of it.
    ///
    /// Deliberately mirrors <see cref="DreamBoxServerLauncher"/>: same dotnet
    /// discovery, same tree-kill, same panel-ready polling. Two launchers with
    /// one shape beats one launcher with two modes.
    /// </summary>
    internal static class LanMonitorLauncher
    {
        private const string MenuRoot = "DreamPark/Multiplayer";
        private const string PidPrefKey = "DreamPark.LanMonitor.Pid";

        /// <summary>Must match config/dev.example.json — one above the relay panel's 7780.</summary>
        private const int DefaultPanelPort = 7781;
        private const double PanelStartupTimeoutSec = 20.0;
        private const double EarlyCrashWindowSec = 5.0;

        private static double s_panelDeadlineTime;
        private static bool s_waitingForPanel;
        private static Process s_process;
        private static double s_launchTime;
        private static readonly System.Collections.Generic.Queue<string> s_recentStderr = new();
        private const int MaxStderrLines = 20;

        // --------------------------------------------------------------------
        // Menu items
        // --------------------------------------------------------------------

        [MenuItem(MenuRoot + "/Start LAN Monitor", isValidateFunction: true)]
        private static bool ValidateStart() => !IsRunning(out _);

        [MenuItem(MenuRoot + "/Start LAN Monitor", priority = 120)]
        private static void StartMonitor()
        {
            if (IsRunning(out var existingPid))
            {
                Debug.Log($"[LAN Monitor] already running (pid {existingPid}); opening panel.");
                OpenPanelUrl();
                return;
            }

            try
            {
                var psi = BuildLaunchStartInfo();
                if (psi == null) return; // dialog already shown

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    EditorUtility.DisplayDialog("DreamPark LAN Monitor",
                        "Failed to start the monitor process.", "OK");
                    return;
                }

                s_process = proc;
                s_recentStderr.Clear();
                s_launchTime = EditorApplication.timeSinceStartup;

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) Debug.Log($"[LAN Monitor] {e.Data}");
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    Debug.LogWarning($"[LAN Monitor] {e.Data}");
                    lock (s_recentStderr)
                    {
                        s_recentStderr.Enqueue(e.Data);
                        while (s_recentStderr.Count > MaxStderrLines) s_recentStderr.Dequeue();
                    }
                };
                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) =>
                {
                    var exitCode = -1;
                    try { exitCode = proc.ExitCode; } catch { }
                    if (exitCode != 0)
                    {
                        var elapsed = EditorApplication.timeSinceStartup - s_launchTime;
                        Debug.LogError($"[LAN Monitor] exited unexpectedly (code {exitCode}, after {elapsed:F1}s)");

                        if (elapsed < EarlyCrashWindowSec)
                        {
                            string stderrSummary;
                            lock (s_recentStderr)
                            {
                                stderrSummary = s_recentStderr.Count > 0
                                    ? string.Join("\n", s_recentStderr)
                                    : "(no stderr captured)";
                            }

                            EditorApplication.delayCall += () =>
                            {
                                EditorUtility.DisplayDialog("DreamPark LAN Monitor — Crash",
                                    $"Monitor exited with code {exitCode} within {elapsed:F1}s.\n\n" +
                                    "Last stderr output:\n" + stderrSummary +
                                    "\n\nTry running it from a terminal for full output:\n" +
                                    "  cd Tools/LanMonitor\n" +
                                    "  dotnet run -- --dev",
                                    "OK");
                            };
                        }
                    }
                    EditorPrefs.DeleteKey(PidPrefKey);
                    s_process = null;
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                EditorPrefs.SetInt(PidPrefKey, proc.Id);
                Debug.Log($"[LAN Monitor] launched (pid {proc.Id}). " +
                          $"Panel: http://127.0.0.1:{DefaultPanelPort} (opening when ready…)");

                BeginWaitForPanel();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LAN Monitor] launch failed: {ex.Message}");
                EditorUtility.DisplayDialog("DreamPark LAN Monitor", ex.Message, "OK");
            }
        }

        [MenuItem(MenuRoot + "/Stop LAN Monitor", isValidateFunction: true)]
        private static bool ValidateStop() => IsRunning(out _);

        [MenuItem(MenuRoot + "/Stop LAN Monitor", priority = 121)]
        private static void StopMonitor()
        {
            if (!IsRunning(out var pid))
            {
                EditorUtility.DisplayDialog("DreamPark LAN Monitor",
                    "No tracked monitor is running.\n\n(If you launched one from a terminal, stop it there.)",
                    "OK");
                return;
            }

            try
            {
                using var proc = Process.GetProcessById(pid);
                KillProcessTree(proc);
                proc.WaitForExit(5000);
                Debug.Log($"[LAN Monitor] stopped (pid {pid}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LAN Monitor] could not stop pid {pid}: {ex.Message}");
            }
            finally
            {
                EditorPrefs.DeleteKey(PidPrefKey);
            }
        }

        [MenuItem(MenuRoot + "/Open Monitor Panel", priority = 210)]
        private static void OpenPanel() => OpenPanelUrl();

        // --------------------------------------------------------------------
        // Launch plumbing
        // --------------------------------------------------------------------

        private static ProcessStartInfo BuildLaunchStartInfo()
        {
            var toolsDir = GetToolsDir();
            if (toolsDir == null) return null;

            var extraArgs = BuildMonitorArgs();

            // 1. `dotnet run` — incremental build for free.
            var dotnetPath = FindDotnet();
            if (dotnetPath != null)
            {
                return new ProcessStartInfo
                {
                    FileName = dotnetPath,
                    Arguments = "run --project \"" + toolsDir + "\" --configuration Release -- --dev" + extraArgs,
                    WorkingDirectory = toolsDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
            }

            // 2. prebuilt self-contained binary
            var rid = CurrentRuntimeId();
            if (rid != null)
            {
                var binaryName = rid.StartsWith("win-") ? "LanMonitor.exe" : "LanMonitor";
                var binaryPath = Path.Combine(toolsDir, "dist", rid, binaryName);
                if (File.Exists(binaryPath))
                {
                    return new ProcessStartInfo
                    {
                        FileName = binaryPath,
                        Arguments = "--dev" + extraArgs,
                        WorkingDirectory = Path.GetDirectoryName(binaryPath),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }
            }

            EditorUtility.DisplayDialog("DreamPark LAN Monitor",
                "Neither the .NET 9 SDK nor a prebuilt binary is available.\n\n" +
                "Pick one:\n" +
                "  • Install the .NET 9 SDK — fastest dev loop:\n" +
                "    https://dotnet.microsoft.com/download/dotnet/9.0\n\n" +
                "  • Or run Tools/LanMonitor/build.sh once on a machine that has\n" +
                "    the SDK and check dist/ into source.\n\n" +
                "Already installed .NET 9? On macOS, Unity launched from Finder/Dock\n" +
                "has a minimal PATH that may not include /usr/local/share/dotnet.\n" +
                "Either launch Unity from Terminal, or symlink dotnet into /usr/local/bin.",
                "OK");
            return null;
        }

        /// <summary>
        /// If a <see cref="NetSessionArbiter"/> in the open scene already knows
        /// which park we're on, hand that to the monitor so it auto-attaches
        /// and follows re-elections without anyone typing anything. Best effort
        /// only — the panel's Pin field is the real control, and an empty
        /// parkId here just means the monitor starts in pick-a-session mode.
        /// </summary>
        private static string BuildMonitorArgs()
        {
            try
            {
                var arbiters = UnityEngine.Object.FindObjectsByType<NetSessionArbiter>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                foreach (var arbiter in arbiters)
                {
                    if (arbiter == null || string.IsNullOrEmpty(arbiter.parkId)) continue;
                    Debug.Log($"[LAN Monitor] following parkId '{arbiter.parkId}' from the NetSessionArbiter in this scene.");
                    return " --park \"" + arbiter.parkId + "\"";
                }
            }
            catch (Exception ex)
            {
                // Never let a scene-inspection failure block the launch.
                Debug.LogWarning($"[LAN Monitor] could not read a parkId from the scene: {ex.Message}");
            }

            return "";
        }

        /// <summary>
        /// Kill the process and its children. `dotnet run` spawns the real
        /// LanMonitor binary as a child, so killing only the wrapper would
        /// leave the monitor holding :7781 and :7700.
        /// </summary>
        private static void KillProcessTree(Process proc)
        {
            var pid = proc.Id;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    using var killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {pid} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    killer?.WaitForExit(3000);
                    return;
                }

                try
                {
                    using var pkill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "pkill",
                        Arguments = $"-TERM -P {pid}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    pkill?.WaitForExit(2000);
                }
                catch { /* pkill may be absent — fall through */ }

                if (!proc.HasExited) proc.Kill();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LAN Monitor] tree-kill for pid {pid} fell back: {ex.Message}");
                try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
            }
        }

        private static string FindDotnet()
        {
            if (TryDotnetAt("dotnet")) return "dotnet";

            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                var candidate = Path.Combine(dotnetRoot,
                    Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate) && TryDotnetAt(candidate)) return candidate;
            }

            string[] candidates;
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    candidates = new[]
                    {
                        "/usr/local/share/dotnet/dotnet",
                        "/usr/local/share/dotnet/x64/dotnet",
                        "/opt/homebrew/bin/dotnet",
                        "/usr/local/bin/dotnet",
                        HomePath(".dotnet/dotnet"),
                    };
                    break;
                case RuntimePlatform.LinuxEditor:
                    candidates = new[]
                    {
                        "/usr/share/dotnet/dotnet",
                        "/usr/bin/dotnet",
                        "/usr/local/bin/dotnet",
                        HomePath(".dotnet/dotnet"),
                    };
                    break;
                case RuntimePlatform.WindowsEditor:
                    candidates = new[]
                    {
                        @"C:\Program Files\dotnet\dotnet.exe",
                        @"C:\Program Files (x86)\dotnet\dotnet.exe",
                        HomePath(@".dotnet\dotnet.exe"),
                    };
                    break;
                default:
                    return null;
            }

            foreach (var path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && TryDotnetAt(path)) return path;
            }

            return null;
        }

        private static string HomePath(string relative)
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                    ?? Environment.GetEnvironmentVariable("USERPROFILE");
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, relative);
        }

        private static bool TryDotnetAt(string exe)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (proc == null) return false;
                proc.WaitForExit(3000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // --------------------------------------------------------------------
        // Panel-ready polling
        // --------------------------------------------------------------------

        private static void BeginWaitForPanel()
        {
            s_panelDeadlineTime = EditorApplication.timeSinceStartup + PanelStartupTimeoutSec;
            if (!s_waitingForPanel)
            {
                s_waitingForPanel = true;
                EditorApplication.update += PanelWaitTick;
            }
        }

        private static void PanelWaitTick()
        {
            if (CanConnect("127.0.0.1", DefaultPanelPort))
            {
                EndWaitForPanel();
                OpenPanelUrl();
                Debug.Log("[LAN Monitor] panel ready.");
                return;
            }

            if (EditorApplication.timeSinceStartup > s_panelDeadlineTime)
            {
                EndWaitForPanel();
                Debug.LogWarning(
                    $"[LAN Monitor] panel didn't come up on :{DefaultPanelPort} within " +
                    $"{PanelStartupTimeoutSec:F0}s. Check the console above — most likely another " +
                    "monitor instance already holds that port.");
            }
        }

        private static void EndWaitForPanel()
        {
            if (!s_waitingForPanel) return;
            s_waitingForPanel = false;
            EditorApplication.update -= PanelWaitTick;
        }

        private static bool CanConnect(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(150))) return false;
                client.EndConnect(ar);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static void OpenPanelUrl()
        {
            // 127.0.0.1 rather than "localhost" — the panel binds IPv4, and on
            // some setups "localhost" resolves to ::1 which HttpListener won't answer.
            Application.OpenURL($"http://127.0.0.1:{DefaultPanelPort}/");
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------

        private static string CurrentRuntimeId()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return "win-x64";
                case RuntimePlatform.OSXEditor: return IsArm64Mac() ? "osx-arm64" : "osx-x64";
                case RuntimePlatform.LinuxEditor: return "linux-x64";
                default: return null;
            }
        }

        private static bool IsArm64Mac()
        {
            var p = SystemInfo.processorType ?? "";
            return p.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Find Tools/LanMonitor. The tool is checked in under dreampark-sdk,
        /// so from dreampark-core we also look in a sibling SDK checkout — the
        /// two repos are normally cloned next to each other, and a core dev
        /// debugging a production session needs this exact tool.
        /// </summary>
        private static string GetToolsDir()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            var local = Path.Combine(projectRoot, "Tools", "LanMonitor");
            if (Directory.Exists(local)) return local;

            var parent = Path.GetDirectoryName(projectRoot);
            if (!string.IsNullOrEmpty(parent))
            {
                foreach (var sibling in Directory.GetDirectories(parent))
                {
                    var name = Path.GetFileName(sibling) ?? "";
                    if (name.IndexOf("dreampark-sdk", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var candidate = Path.Combine(sibling, "Tools", "LanMonitor");
                    if (Directory.Exists(candidate))
                    {
                        Debug.Log($"[LAN Monitor] using the SDK checkout at {candidate}.");
                        return candidate;
                    }
                }
            }

            EditorUtility.DisplayDialog("DreamPark LAN Monitor",
                "Could not find Tools/LanMonitor/.\n\n" +
                "Looked in:\n" +
                "  " + local + "\n" +
                "  a sibling dreampark-sdk checkout next to this project\n\n" +
                "The tool lives in dreampark-sdk. Clone it beside this project, " +
                "or copy Tools/LanMonitor/ into this project root.",
                "OK");
            return null;
        }

        private static bool IsRunning(out int pid)
        {
            pid = EditorPrefs.GetInt(PidPrefKey, 0);
            if (pid <= 0) return false;

            try
            {
                using var proc = Process.GetProcessById(pid);
                return proc != null && !proc.HasExited;
            }
            catch
            {
                EditorPrefs.DeleteKey(PidPrefKey);
                return false;
            }
        }
    }
}
#endif
