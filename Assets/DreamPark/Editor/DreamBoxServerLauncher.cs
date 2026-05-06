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
    /// Unity Editor menu for the bundled DreamBox dev server (Tools/DreamBoxServer/).
    ///
    /// Single-click flow (the common case):
    ///   DreamPark → Multiplayer → Start Local Server
    ///     1. If a server is already running, just open the panel.
    ///     2. Else pick a launch path:
    ///          a. `dotnet run` against the project  (preferred — incremental build, .NET SDK required)
    ///          b. Prebuilt self-contained binary from Tools/DreamBoxServer/dist/&lt;rid&gt;/
    ///     3. Poll the panel port in the background; open the browser when it's up.
    ///
    /// Secondary commands:
    ///   Stop Server           kill the tracked process
    ///   Open Control Panel    just open the browser (if already running)
    ///   Rebuild Binaries      force a full self-contained publish (for devs w/o .NET SDK)
    ///   Reveal Tools Folder   show Tools/DreamBoxServer/ in Finder/Explorer
    /// </summary>
    internal static class DreamBoxServerLauncher
    {
        private const string MenuRoot = "DreamPark/Multiplayer";
        private const string PidPrefKey = "DreamPark.DevServer.Pid";

        // Must match dev.example.json.
        private const int DefaultPanelPort = 7780;
        // How long we'll wait for the panel port to come up after launching.
        private const double PanelStartupTimeoutSec = 20.0;

        // Background-poll state (static because Editor menu callbacks can't carry state).
        private static double s_panelDeadlineTime;
        private static bool s_waitingForPanel;

        // --------------------------------------------------------------------
        // Menu items
        // --------------------------------------------------------------------

        // Greys out "Start" when the server is already running, so only one
        // of Start/Stop is ever clickable at a time.
        [MenuItem(MenuRoot + "/Start Local Server", isValidateFunction: true)]
        private static bool ValidateStartLocalServer() => !IsTrackedServerRunning(out _);

        [MenuItem(MenuRoot + "/Start Local Server", priority = 100)]
        private static void StartLocalServer()
        {
            if (IsTrackedServerRunning(out var existingPid))
            {
                Debug.Log($"[DreamBox Dev Server] already running (pid {existingPid}); opening control panel.");
                OpenControlPanelUrl();
                return;
            }

            try
            {
                var psi = BuildLaunchStartInfo();
                if (psi == null) return; // launch aborted, dialog already shown

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    EditorUtility.DisplayDialog("DreamBox Dev Server",
                        "Failed to start the server process.", "OK");
                    return;
                }

                EditorPrefs.SetInt(PidPrefKey, proc.Id);
                Debug.Log($"[DreamBox Dev Server] launched (pid {proc.Id}). " +
                          $"Control panel: http://localhost:{DefaultPanelPort} (opening when ready…)");

                BeginWaitForPanel();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DreamBox Dev Server] launch failed: {ex.Message}");
                EditorUtility.DisplayDialog("DreamBox Dev Server", ex.Message, "OK");
            }
        }

        // Greys out "Stop" when nothing is running.
        [MenuItem(MenuRoot + "/Stop Local Server", isValidateFunction: true)]
        private static bool ValidateStopLocalServer() => IsTrackedServerRunning(out _);

        [MenuItem(MenuRoot + "/Stop Local Server", priority = 101)]
        private static void StopLocalServer()
        {
            if (!IsTrackedServerRunning(out var pid))
            {
                EditorUtility.DisplayDialog("DreamBox Dev Server",
                    "No tracked dev server is running.\n\n(If you launched one from a terminal, stop it there.)",
                    "OK");
                return;
            }

            try
            {
                using var proc = Process.GetProcessById(pid);
                KillProcessTree(proc);
                proc.WaitForExit(5000);
                Debug.Log($"[DreamBox Dev Server] stopped (pid {pid}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DreamBox Dev Server] could not stop pid {pid}: {ex.Message}");
            }
            finally
            {
                EditorPrefs.DeleteKey(PidPrefKey);
            }
        }

        /// <summary>
        /// Kill a process and its descendants. Unity's .NET profile doesn't ship
        /// the <c>Process.Kill(bool entireProcessTree)</c> overload (that's .NET 5+),
        /// so we shell out to the platform's tree-kill command.
        ///
        /// Needed because <c>dotnet run</c> spawns the actual <c>DreamBoxRelay</c>
        /// binary as a child — killing only the wrapper leaves the relay running.
        /// </summary>
        private static void KillProcessTree(Process proc)
        {
            var pid = proc.Id;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // /T = kill tree, /F = force
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

                // macOS / Linux: kill the child process group first, then the parent.
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
                catch { /* pkill may be absent on some distros — fall through */ }

                if (!proc.HasExited) proc.Kill();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DreamBox Dev Server] tree-kill for pid {pid} fell back: {ex.Message}");
                try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
            }
        }

        // priority gap >10 creates a visual separator in the menu.

        [MenuItem(MenuRoot + "/Open Control Panel", priority = 200)]
        private static void OpenControlPanel() => OpenControlPanelUrl();

        [MenuItem(MenuRoot + "/Rebuild Binaries", priority = 201)]
        private static void RebuildBinaries()
        {
            var toolsDir = GetToolsDir();
            if (toolsDir == null) return;

            ProcessStartInfo psi;
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-ExecutionPolicy Bypass -File \"" + Path.Combine(toolsDir, "build.ps1") + "\"",
                    WorkingDirectory = toolsDir,
                    UseShellExecute = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "\"" + Path.Combine(toolsDir, "build.sh") + "\"",
                    WorkingDirectory = toolsDir,
                    UseShellExecute = true
                };
            }

            try
            {
                var proc = Process.Start(psi);
                if (proc == null) throw new Exception("Process.Start returned null");
                Debug.Log($"[DreamBox Dev Server] build started (pid {proc.Id}). Watch the shell window for output.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DreamBox Dev Server] build launch failed: {ex.Message}");
                EditorUtility.DisplayDialog("DreamBox Dev Server",
                    "Could not start the build script.\n\n" + ex.Message +
                    "\n\nYou can also run it manually:\n  cd Tools/DreamBoxServer\n  ./build.sh",
                    "OK");
            }
        }

        [MenuItem(MenuRoot + "/Reveal Tools Folder", priority = 300)]
        private static void RevealToolsFolder()
        {
            var toolsDir = GetToolsDir();
            if (toolsDir == null) return;
            EditorUtility.RevealInFinder(toolsDir);
        }

        // --------------------------------------------------------------------
        // Launch plumbing
        // --------------------------------------------------------------------

        /// <summary>
        /// Decide how to launch. Prefer `dotnet run` (fastest dev loop, incremental
        /// build is automatic). Fall back to a prebuilt self-contained binary.
        /// Returns null if neither is viable (and shows a dialog).
        /// </summary>
        private static ProcessStartInfo BuildLaunchStartInfo()
        {
            var toolsDir = GetToolsDir();
            if (toolsDir == null) return null;

            // 1. `dotnet run` — handles build-if-needed for free
            var dotnetPath = FindDotnet();
            if (dotnetPath != null)
            {
                return new ProcessStartInfo
                {
                    FileName = dotnetPath,
                    Arguments = "run --project \"" + toolsDir + "\" --configuration Release -- --dev",
                    WorkingDirectory = toolsDir,
                    UseShellExecute = true // opens its own console window — visible logs
                };
            }

            // 2. prebuilt self-contained binary (already `Rebuild Binaries` output)
            var rid = CurrentRuntimeId();
            if (rid != null)
            {
                var binaryName = rid.StartsWith("win-") ? "DreamBoxRelay.exe" : "DreamBoxRelay";
                var binaryPath = Path.Combine(toolsDir, "dist", rid, binaryName);
                if (File.Exists(binaryPath))
                {
                    return new ProcessStartInfo
                    {
                        FileName = binaryPath,
                        Arguments = "--dev",
                        WorkingDirectory = Path.GetDirectoryName(binaryPath),
                        UseShellExecute = true
                    };
                }
            }

            // 3. nothing works — guide the developer
            var msg = "Neither the .NET 9 SDK nor a prebuilt binary is available.\n\n" +
                      "Pick one:\n" +
                      "  • Install the .NET 9 SDK — fastest dev loop:\n" +
                      "    https://dotnet.microsoft.com/download/dotnet/9.0\n\n" +
                      "  • Or run DreamPark → Multiplayer → Rebuild Binaries once\n" +
                      "    on a machine that has the SDK, check the `dist/` folder into\n" +
                      "    source, and this menu will use the prebuilt binary from then on.\n\n" +
                      "Already installed .NET 9? On macOS, Unity launched from Finder/Dock\n" +
                      "has a minimal PATH that may not include /usr/local/share/dotnet.\n" +
                      "Either launch Unity from Terminal, or symlink dotnet into /usr/local/bin.";
            EditorUtility.DisplayDialog("DreamBox Dev Server", msg, "OK");
            return null;
        }

        /// <summary>
        /// Locate the <c>dotnet</c> executable. On macOS in particular, Unity
        /// launched from Finder/Dock inherits a minimal PATH that often doesn't
        /// include the SDK install location, so we fall back to a list of
        /// well-known install paths and the <c>DOTNET_ROOT</c> env var before
        /// giving up.
        /// </summary>
        /// <returns>Full path to dotnet, or null if not found.</returns>
        private static string FindDotnet()
        {
            // First try the plain "dotnet" — works if it's on PATH.
            if (TryDotnetAt("dotnet")) return "dotnet";

            // DOTNET_ROOT is set by the official .NET installer.
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                var candidate = Path.Combine(dotnetRoot,
                    Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate) && TryDotnetAt(candidate)) return candidate;
            }

            // Well-known install locations per platform.
            string[] candidates;
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    candidates = new[]
                    {
                        "/usr/local/share/dotnet/dotnet",             // official installer (Intel + ARM universal)
                        "/usr/local/share/dotnet/x64/dotnet",         // x64 slice on Apple Silicon
                        "/opt/homebrew/bin/dotnet",                   // Homebrew on Apple Silicon
                        "/usr/local/bin/dotnet",                      // Homebrew on Intel
                        HomePath(".dotnet/dotnet"),                   // user-scoped install script
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
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && TryDotnetAt(path))
                    return path;
            }

            return null;
        }

        private static string HomePath(string relative)
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                    ?? Environment.GetEnvironmentVariable("USERPROFILE");
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, relative);
        }

        /// <summary>
        /// Runs <c>{exe} --version</c> and returns true on exit code 0.
        /// </summary>
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
            // Probe every frame — cheap TCP connect attempt, no HTTP parsing needed.
            if (CanConnect("127.0.0.1", DefaultPanelPort))
            {
                EndWaitForPanel();
                OpenControlPanelUrl();
                Debug.Log("[DreamBox Dev Server] panel ready.");
                return;
            }

            if (EditorApplication.timeSinceStartup > s_panelDeadlineTime)
            {
                EndWaitForPanel();
                Debug.LogWarning(
                    $"[DreamBox Dev Server] control panel didn't come up on :{DefaultPanelPort} " +
                    $"within {PanelStartupTimeoutSec:F0}s. Check the server console for errors " +
                    "(maybe port 7777 or 7780 is already in use).");
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
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(150)))
                    return false;
                client.EndConnect(ar);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static void OpenControlPanelUrl()
        {
            // Using 127.0.0.1 (not "localhost") — the server binds to 127.0.0.1,
            // and on some setups "localhost" resolves to ::1 (IPv6) which HttpListener
            // on IPv4 won't answer. 127.0.0.1 also sidesteps any stale browser cache
            // keyed to "localhost".
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
                case RuntimePlatform.OSXEditor:
                    return IsArm64Mac() ? "osx-arm64" : "osx-x64";
                case RuntimePlatform.LinuxEditor: return "linux-x64";
                default: return null;
            }
        }

        private static bool IsArm64Mac()
        {
            // SystemInfo.processorType contains "Apple" on Apple Silicon.
            var p = SystemInfo.processorType ?? "";
            return p.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetToolsDir()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var toolsDir = Path.Combine(projectRoot, "Tools", "DreamBoxServer");
            if (!Directory.Exists(toolsDir))
            {
                EditorUtility.DisplayDialog("DreamBox Dev Server",
                    "Could not find Tools/DreamBoxServer/ at the project root.\n\n" +
                    "Expected: " + toolsDir,
                    "OK");
                return null;
            }
            return toolsDir;
        }

        private static bool IsTrackedServerRunning(out int pid)
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
