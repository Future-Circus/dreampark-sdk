#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DreamPark
{
    // Detects a clone made without Git LFS and offers to repair it in place.
    //
    // Most of this repo's art, audio, models and native plugins are stored in
    // Git LFS. Cloning without the LFS filter installed still "succeeds" — git
    // just writes a ~130-byte text pointer where each binary should be. Unity
    // then imports those pointers as corrupt assets, and the failure surfaces
    // much later as pink materials, silent audio, missing meshes or an XLua
    // native plugin that won't load. Nothing in the error text says "LFS", so
    // creators lose real time to it. This check names the actual problem on
    // first open and can fix it without leaving the editor.
    //
    // Runs once per editor session (SessionState), same nag-cadence as
    // PlaceholderContentDetector: a fresh Unity launch prompts again if the
    // repo is still broken, but a domain reload does not re-stack the dialog.
    // Also available on demand via DreamPark → Troubleshooting → Check Git LFS.
    [InitializeOnLoad]
    internal static class GitLfsCheck
    {
        private const string SessionFlag = "DreamPark.GitLfsCheck.ShownThisSession";

        // First line of every LFS pointer file, per the v1 pointer spec.
        private const string PointerMagic = "version https://git-lfs.github.com/spec/v1";

        // Pointer files are tiny (~130 bytes). Anything larger is real content,
        // so a cheap length test skips reading ~all of the project's bytes.
        private const int PointerMaxBytes = 1024;

        private const int MaxExamplesLogged = 10;
        private const string InstallUrl = "https://git-lfs.com";

        static GitLfsCheck()
        {
            // Defer past startup so we don't dialog while Unity is still
            // compiling or refreshing the AssetDatabase on first launch.
            EditorApplication.delayCall += AutoCheck;
        }

        [MenuItem("DreamPark/Troubleshooting/Check Git LFS", false, 211)]
        private static void MenuCheck()
        {
            Run(interactive: true);
        }

        private static void AutoCheck()
        {
            if (SessionState.GetBool(SessionFlag, false)) return;
            if (EditorApplication.isCompiling) return;
            if (EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            // Mark before running — if this throws or the user dismisses it,
            // we don't want it back on the very next domain reload.
            SessionState.SetBool(SessionFlag, true);
            Run(interactive: false);
        }

        // --------------------------------------------------------------------
        // Check
        // --------------------------------------------------------------------

        private static void Run(bool interactive)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return;

            // A zip download has no .git, so there is nothing LFS can repair.
            // Say so explicitly rather than offering a fix that cannot work.
            if (!Directory.Exists(Path.Combine(projectRoot, ".git")))
            {
                if (interactive)
                {
                    EditorUtility.DisplayDialog(
                        "Not a git clone",
                        "This project folder has no .git directory, so Git LFS can't restore anything here.\n\n" +
                        "If you downloaded the SDK as a ZIP, the binary assets in it are placeholders. " +
                        "Install Git LFS, then clone the repo instead:\n\n" +
                        "    git lfs install\n" +
                        "    git clone https://github.com/Future-Circus/dreampark-sdk.git MyGame",
                        "OK");
                }
                return;
            }

            List<string> pointers = FindPointerFiles(projectRoot);

            if (pointers.Count == 0)
            {
                if (interactive)
                {
                    EditorUtility.DisplayDialog(
                        "Git LFS",
                        "All good — every LFS-tracked asset in this project has its real content.",
                        "OK");
                }
                return;
            }

            LogPointers(pointers);

            string message =
                $"{pointers.Count} asset{(pointers.Count == 1 ? " is" : "s are")} still a Git LFS placeholder " +
                "instead of the real file.\n\n" +
                "This happens when the repo is cloned without Git LFS installed. Textures, audio, models and " +
                "the XLua native plugin will be broken until they're downloaded.\n\n" +
                "Fix it now? This runs:\n\n" +
                "    git lfs install\n" +
                "    git lfs pull\n\n" +
                "Only placeholder files are replaced; your own edits are left alone. " +
                "(See the Console for the full list.)";

            int choice = EditorUtility.DisplayDialogComplex(
                "Git LFS files not downloaded",
                message,
                "Download Now",
                "Not Now",
                "How to Install Git LFS");

            if (choice == 0) Repair(projectRoot);
            else if (choice == 2) ShowInstallHelp();
        }

        /// <summary>
        /// Walks Assets/ and Packages/ for files whose extension is LFS-tracked
        /// in .gitattributes and whose contents are still a pointer stub.
        /// </summary>
        private static List<string> FindPointerFiles(string projectRoot)
        {
            var results = new List<string>();
            HashSet<string> tracked = ReadTrackedExtensions(projectRoot);
            if (tracked.Count == 0) return results;

            foreach (string root in new[] { "Assets", "Packages" })
            {
                string dir = Path.Combine(projectRoot, root);
                if (!Directory.Exists(dir)) continue;

                try
                {
                    // Enumeration is lazy, so an unreadable subdirectory throws
                    // from inside the loop — the whole walk has to be guarded.
                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Length == 0 || !tracked.Contains(ext.ToLowerInvariant())) continue;

                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Length == 0 || info.Length > PointerMaxBytes) continue;
                            if (!StartsWithPointerMagic(file)) continue;
                            results.Add(GetRelativePath(projectRoot, file));
                        }
                        catch
                        {
                            // Unreadable / vanished mid-scan — nothing to report.
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Git LFS] Could not finish scanning {root}: {e.Message}");
                }
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        /// <summary>
        /// Pulls the <c>*.ext</c> patterns marked <c>filter=lfs</c> out of
        /// .gitattributes, so this check stays correct as tracking rules change
        /// rather than hardcoding a list that drifts.
        /// </summary>
        private static HashSet<string> ReadTrackedExtensions(string projectRoot)
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(projectRoot, ".gitattributes");
            if (!File.Exists(path)) return extensions;

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.IndexOf("filter=lfs", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string pattern = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    // Only simple "*.ext" patterns; path patterns like
                    // "ServerData/**" don't map to an extension test.
                    if (pattern.StartsWith("*.") && pattern.IndexOf('/') < 0 && pattern.Length > 2)
                        extensions.Add(pattern.Substring(1).ToLowerInvariant());
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Git LFS] Could not read .gitattributes: {e.Message}");
            }

            return extensions;
        }

        private static bool StartsWithPointerMagic(string file)
        {
            var magic = Encoding.ASCII.GetBytes(PointerMagic);
            var head = new byte[magic.Length];

            using (var stream = File.OpenRead(file))
            {
                int read = 0;
                while (read < head.Length)
                {
                    int n = stream.Read(head, read, head.Length - read);
                    if (n <= 0) return false;
                    read += n;
                }
            }

            for (int i = 0; i < magic.Length; i++)
                if (head[i] != magic[i]) return false;

            return true;
        }

        private static void LogPointers(List<string> pointers)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Git LFS] {pointers.Count} asset(s) are Git LFS placeholders, not real files:");
            for (int i = 0; i < pointers.Count && i < MaxExamplesLogged; i++)
                sb.AppendLine("  " + pointers[i]);
            if (pointers.Count > MaxExamplesLogged)
                sb.AppendLine($"  …and {pointers.Count - MaxExamplesLogged} more.");
            sb.Append("Fix with: DreamPark → Troubleshooting → Check Git LFS");
            Debug.LogWarning(sb.ToString());
        }

        // --------------------------------------------------------------------
        // Repair
        // --------------------------------------------------------------------

        private static void Repair(string projectRoot)
        {
            string git = FindGit();
            if (git == null)
            {
                EditorUtility.DisplayDialog(
                    "git not found",
                    "Unity couldn't find the git executable.\n\n" +
                    "Install git (and Git LFS), then either restart Unity or run " +
                    "`git lfs install && git lfs pull` in the project folder yourself.",
                    "OK");
                Application.OpenURL(InstallUrl);
                return;
            }

            // `git lfs version` is the cheapest proof the LFS extension is
            // actually on this machine — `git lfs` on a machine without it
            // fails as an unknown subcommand.
            if (!RunGit(git, projectRoot, "lfs version", 15_000, out string versionOut, out _))
            {
                ShowInstallHelp();
                return;
            }
            Debug.Log($"[Git LFS] Found {versionOut.Trim()}");

            try
            {
                // Global on purpose: this is the step whose absence caused the
                // bad clone, and setting it here means the creator's *next*
                // clone of any LFS repo works too.
                var install = RunGitWithProgress(git, projectRoot, "lfs install",
                    "Git LFS", "Registering the LFS filter…", 60_000);

                if (install.Cancelled) return;
                if (!install.Success)
                {
                    EditorUtility.DisplayDialog("git lfs install failed", Trim(install.Stderr), "OK");
                    return;
                }

                var pull = RunGitWithProgress(git, projectRoot, "lfs pull",
                    "Git LFS", "Downloading assets — this can take a few minutes…", 30 * 60_000);

                if (pull.Cancelled)
                {
                    Debug.LogWarning("[Git LFS] Download cancelled. Some assets are still placeholders — " +
                                     "rerun DreamPark → Troubleshooting → Check Git LFS when you're ready.");
                    return;
                }
                if (!pull.Success)
                {
                    EditorUtility.DisplayDialog("git lfs pull failed", Trim(pull.Stderr), "OK");
                    return;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            List<string> remaining = FindPointerFiles(projectRoot);
            if (remaining.Count == 0)
            {
                Debug.Log("[Git LFS] All LFS assets downloaded. Unity is reimporting them now.");
                EditorUtility.DisplayDialog(
                    "Git LFS",
                    "Done — all assets downloaded and reimporting.\n\n" +
                    "If anything still looks wrong, close Unity, delete the project's Library folder, and reopen.",
                    "OK");
            }
            else
            {
                LogPointers(remaining);
                EditorUtility.DisplayDialog(
                    "Git LFS",
                    $"{remaining.Count} file(s) are still placeholders. See the Console for the list — " +
                    "this usually means the LFS objects aren't on the remote, or the fetch was interrupted.",
                    "OK");
            }
        }

        private static void ShowInstallHelp()
        {
            EditorUtility.DisplayDialog(
                "Install Git LFS",
                "Git LFS isn't installed on this machine.\n\n" +
                "macOS:    brew install git-lfs\n" +
                "Windows:  winget install GitHub.GitLFS\n" +
                "Linux:    sudo apt install git-lfs\n\n" +
                "Then run DreamPark → Troubleshooting → Check Git LFS again.\n\n" +
                "Opening git-lfs.com for the installer.",
                "OK");
            Application.OpenURL(InstallUrl);
        }

        // --------------------------------------------------------------------
        // Process plumbing
        // --------------------------------------------------------------------

        internal struct GitResult
        {
            public bool Success;
            public bool Cancelled;
            public string Stdout;
            public string Stderr;
        }

        /// <summary>
        /// Runs git while pumping a cancellable progress bar.
        ///
        /// `git lfs pull` routinely runs for minutes. Blocking the main thread
        /// on WaitForExit leaves Unity beachballed behind a progress bar that
        /// never repaints, which reads as a hang — so poll instead, repaint
        /// each tick, and surface git's own progress output as the label.
        /// </summary>
        private static GitResult RunGitWithProgress(string git, string workingDir, string args,
                                                    string title, string info, int timeoutMs)
        {
            var result = new GitResult { Stdout = string.Empty, Stderr = string.Empty };
            var sync = new object();
            var outBuf = new StringBuilder();
            var errBuf = new StringBuilder();
            string lastLine = info;

            try
            {
                var psi = BuildStartInfo(git, workingDir, args);
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    result.Stderr = "Could not start git.";
                    return result;
                }

                // git writes transfer progress to stderr, so both streams feed
                // the label; whichever spoke last is the most useful thing to
                // show. Handlers fire on threadpool threads — hence the lock.
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (sync) { outBuf.AppendLine(e.Data); if (e.Data.Trim().Length > 0) lastLine = e.Data.Trim(); }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (sync) { errBuf.AppendLine(e.Data); if (e.Data.Trim().Length > 0) lastLine = e.Data.Trim(); }
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var clock = Stopwatch.StartNew();
                while (!proc.HasExited)
                {
                    if (clock.ElapsedMilliseconds > timeoutMs)
                    {
                        TryKill(proc);
                        result.Stderr = $"`git {args}` timed out after {timeoutMs / 1000}s.";
                        return result;
                    }

                    string label;
                    lock (sync) { label = lastLine; }
                    if (label.Length > 90) label = label.Substring(0, 90) + "…";

                    // Indeterminate: git-lfs percentages are per-file, not
                    // per-job, so a sweeping bar is honest and a computed one
                    // would not be.
                    float pulse = (clock.ElapsedMilliseconds % 2000) / 2000f;

                    if (EditorUtility.DisplayCancelableProgressBar(title, label, pulse))
                    {
                        TryKill(proc);
                        result.Cancelled = true;
                        return result;
                    }

                    System.Threading.Thread.Sleep(100);
                }

                // WaitForExit(int) can return before the async readers drain;
                // the parameterless overload is what guarantees a full flush.
                proc.WaitForExit();

                lock (sync)
                {
                    result.Stdout = outBuf.ToString();
                    result.Stderr = errBuf.ToString();
                }
                result.Success = proc.ExitCode == 0;
                return result;
            }
            catch (Exception e)
            {
                result.Stderr = e.Message;
                return result;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void TryKill(Process proc)
        {
            try { proc.Kill(); } catch { /* already gone */ }
        }

        private static ProcessStartInfo BuildStartInfo(string git, string workingDir, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = git,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Unity launched from Finder/Explorer inherits a minimal PATH,
            // which is how `git` ends up unable to find its own `git-lfs`
            // helper even though the terminal finds both. Widen it.
            psi.EnvironmentVariables["PATH"] = BuildPath();

            // Never let a credential prompt block the editor on a hidden
            // process nobody can type into.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

            return psi;
        }

        /// <summary>Short, blocking git call — for probes that finish instantly.</summary>
        private static bool RunGit(string git, string workingDir, string args, int timeoutMs,
                                   out string stdout, out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;

            try
            {
                using var proc = Process.Start(BuildStartInfo(git, workingDir, args));
                if (proc == null) return false;

                var sync = new object();
                var outBuf = new StringBuilder();
                var errBuf = new StringBuilder();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sync) outBuf.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sync) errBuf.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(timeoutMs))
                {
                    TryKill(proc);
                    stderr = $"`git {args}` timed out after {timeoutMs / 1000}s.";
                    return false;
                }

                // Guarantees the async readers have drained before we read them.
                proc.WaitForExit();

                lock (sync)
                {
                    stdout = outBuf.ToString();
                    stderr = errBuf.ToString();
                }
                return proc.ExitCode == 0;
            }
            catch (Exception e)
            {
                stderr = e.Message;
                return false;
            }
        }

        private static string BuildPath()
        {
            var parts = new List<string>();
            string inherited = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(inherited)) parts.Add(inherited);

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                parts.Add(@"C:\Program Files\Git\cmd");
                parts.Add(@"C:\Program Files\Git\bin");
                return string.Join(";", parts);
            }

            parts.Add("/opt/homebrew/bin");   // Homebrew on Apple Silicon
            parts.Add("/usr/local/bin");      // Homebrew on Intel, most installers
            parts.Add("/usr/bin");
            parts.Add("/bin");
            return string.Join(":", parts);
        }

        private static string FindGit()
        {
            string[] candidates = Application.platform == RuntimePlatform.WindowsEditor
                ? new[]
                {
                    "git.exe",
                    @"C:\Program Files\Git\cmd\git.exe",
                    @"C:\Program Files (x86)\Git\cmd\git.exe",
                }
                : new[]
                {
                    "git",
                    "/opt/homebrew/bin/git",
                    "/usr/local/bin/git",
                    "/usr/bin/git",
                };

            foreach (string candidate in candidates)
            {
                if (RunGit(candidate, Application.dataPath, "--version", 5000, out _, out _))
                    return candidate;
            }
            return null;
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------

        private static string GetRelativePath(string root, string full)
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            return full.Replace('\\', '/');
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "No details reported. See the Console.";
            text = text.Trim();
            return text.Length > 1200 ? text.Substring(0, 1200) + "\n…" : text;
        }
    }
}
#endif
