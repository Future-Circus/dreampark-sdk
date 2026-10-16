#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamPark.ParkBuilder;
using DreamPark.ParkSim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Playtest
{
    /// <summary>
    /// One AI playtest run: Claude plays the game through <see cref="PlaytestTools"/>,
    /// reports issues as it confirms them, optionally patches the creator's Lua and
    /// restarts Play mode to re-verify, and writes a Markdown report.
    ///
    /// Entering Play mode reloads the scripting domain, which kills any running
    /// async loop. So the whole conversation lives in a file under Library/, and the
    /// loop is resumable: Start → (reload) → EnteredPlayMode → Begin, and likewise
    /// for a mid-run restart_playtest.
    /// </summary>
    [InitializeOnLoad]
    public static class PlaytestSession
    {
        public enum Status { Idle, WaitingForPlayMode, WaitingForWorld, Running, Restarting, Finished, Stopped, Failed }

        [Serializable]
        public class Entry
        {
            public string kind;   // say | thinking | action | result | issue | fix | system | error
            public string text;
            public string image;  // path to a saved jpg, if any
        }

        [Serializable]
        public class Issue
        {
            public string severity, title, what_happened, expected, evidence, suggested_fix, script, screenshot;
        }

        [Serializable]
        public class Fix
        {
            public string path, rationale, old_string, new_string, backup;
        }

        [Serializable]
        public class State
        {
            public Status status = Status.Idle;
            public string contentId, goal, model, effort, baseUrl;
            public int maxSteps = 60;
            public bool freezeTime = true, autoScreenshot = true, bodyProbe = true, allowFixes;
            public string runDir, startedUtc, finishedUtc;
            public int steps, nudges, restarts, shots;
            public bool budgetWarned;
            public string verdict, summary, error, reportPath;
            public string pendingRestartToolUseId;
            public JArray pendingResults;
            public JArray messages = new JArray();
            public List<Entry> entries = new List<Entry>();
            public List<Issue> issues = new List<Issue>();
            public List<Fix> fixes = new List<Fix>();
        }

        public struct Options
        {
            public string contentId, goal, model, effort, baseUrl;
            public int maxSteps;
            public bool freezeTime, autoScreenshot, bodyProbe, allowFixes;

            public static Options FromSettings() => new Options
            {
                contentId = string.IsNullOrEmpty(PlaytestSettings.ContentId) ? PlaytestBriefing.DefaultContentId() : PlaytestSettings.ContentId,
                goal = PlaytestSettings.Goal,
                model = PlaytestSettings.Model,
                effort = PlaytestSettings.Effort,
                baseUrl = PlaytestSettings.BaseUrl,
                maxSteps = PlaytestSettings.MaxSteps,
                freezeTime = PlaytestSettings.FreezeTime,
                autoScreenshot = PlaytestSettings.AutoScreenshot,
                bodyProbe = PlaytestSettings.BodyProbe,
                allowFixes = PlaytestSettings.AllowFixes,
            };
        }

        const string StatePath = "Library/DreamParkPlaytest/session.json";
        const string ReportsRoot = "Logs/DreamParkPlaytest";
        const int MaxEntries = 600;
        const int KeepScreenshots = 5;
        const int PruneAtScreenshots = 12;

        public static State Current { get; private set; } = new State();
        public static event Action Changed;
        public static byte[] LastScreenshot { get; private set; }

        public static bool IsActive =>
            Current.status == Status.WaitingForPlayMode || Current.status == Status.WaitingForWorld ||
            Current.status == Status.Running || Current.status == Status.Restarting;

        static CancellationTokenSource _cts;
        static PlaytestClaudeClient _client;
        static PlaytestTools _tools;
        static bool _finishRequested;

        static PlaytestSession()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Load();
            // A reload that lands mid-run (a C# recompile during Play) kills the loop.
            // WaitingForPlayMode and Restarting are expected to survive a reload:
            // EnteredPlayMode picks them up.
            if (Current.status == Status.Running || Current.status == Status.WaitingForWorld)
            {
                Current.status = Status.Stopped;
                Current.error = "Interrupted by a script reload.";
                AddEntry("error", Current.error);
                WriteReport();
                Save();
            }
        }

        // ── Public control ────────────────────────────────────────────────

        public static string Start(Options o)
        {
            // A run still "waiting for Play mode" while the Editor is idle in Edit mode
            // is stale (Play was cancelled or failed to start); let a new run replace it.
            bool stale = Current.status == Status.WaitingForPlayMode && !EditorApplication.isPlayingOrWillChangePlaymode;
            if (IsActive && !stale) return "A playtest is already running.";
            // A proxy base URL authenticates on its own, so the key is optional there.
            if (string.IsNullOrEmpty(PlaytestSettings.ApiKey) && string.IsNullOrWhiteSpace(o.baseUrl))
                return "No Anthropic API key. Set ANTHROPIC_API_KEY or enter one in DreamPark → AI Playtester.";

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            Current = new State
            {
                status = Status.WaitingForPlayMode,
                contentId = o.contentId,
                goal = o.goal,
                model = string.IsNullOrEmpty(o.model) ? PlaytestSettings.Models[0] : o.model,
                effort = string.IsNullOrEmpty(o.effort) ? "high" : o.effort,
                baseUrl = o.baseUrl,
                maxSteps = Math.Max(5, o.maxSteps),
                freezeTime = o.freezeTime,
                autoScreenshot = o.autoScreenshot,
                bodyProbe = o.bodyProbe,
                allowFixes = o.allowFixes,
                runDir = $"{ReportsRoot}/{stamp}-{o.contentId}",
                startedUtc = DateTime.UtcNow.ToString("o"),
            };
            Directory.CreateDirectory(Current.runDir);
            LastScreenshot = null;
            AddEntry("system", $"Playtesting {o.contentId} with {Current.model} (effort {Current.effort}, {Current.maxSteps} steps).");
            Save();

            if (EditorApplication.isPlaying)
            {
                _ = Begin();
            }
            else
            {
                AddEntry("system", "Entering Play mode…");
                // Set directly: delayCall only runs when the Editor ticks, and an
                // unfocused Editor driven from the CLI may not tick until clicked.
                EditorApplication.isPlaying = true;
            }
            return null;
        }

        public static void Stop(string reason = "Stopped by the creator.")
        {
            if (!IsActive) return;
            _cts?.Cancel();
            _client?.Abort();
            Current.status = Status.Stopped;
            Current.error = reason;
            AddEntry("system", reason);
            TearDownWorld();
            WriteReport();
            Save();
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (Current.status == Status.WaitingForPlayMode || Current.status == Status.Restarting) _ = Begin();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    PlaytestClock.Thaw();
                    if (Current.status == Status.Running || Current.status == Status.WaitingForWorld)
                        Stop("Play mode was exited.");
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    if (Current.status == Status.Restarting)
                        EditorApplication.isPlaying = true;
                    break;
            }
        }

        // ── Run loop ──────────────────────────────────────────────────────

        static async Task Begin()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var cancel = _cts.Token;
            _finishRequested = false;
            Current.status = Status.WaitingForWorld;
            AddEntry("system", "Waiting for the park to finish loading…");
            Save();

            try
            {
                if (!await WaitForWorld(cancel)) return;

                PlaytestObserver.Begin();
                PlaytestAvatar.Spawn(Current.bodyProbe);
                _tools = new PlaytestTools(Current.freezeTime, Current.autoScreenshot);
                _client = new PlaytestClaudeClient(PlaytestSettings.ApiKey, Current.baseUrl);
                if (Current.freezeTime) PlaytestClock.Freeze();

                if (Current.messages.Count == 0) Current.messages.Add(FirstUserMessage());
                else if (Current.pendingRestartToolUseId != null) Current.messages.Add(ResumeAfterRestart());

                Current.status = Status.Running;
                AddEntry("system", "Playing.");
                Save();
                await RunLoop(cancel);
            }
            catch (OperationCanceledException) { /* Stop() already recorded why */ }
            catch (Exception e)
            {
                Debug.LogException(e);
                Fail(e.Message);
            }
        }

        static async Task<bool> WaitForWorld(CancellationToken cancel)
        {
            double deadline = EditorApplication.timeSinceStartup + 120;
            double readySince = -1;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                cancel.ThrowIfCancellationRequested();
                if (!Application.isPlaying) return false;
                bool ready = Camera.main != null && LevelObjectManager.objectsEnabled && !ParkSimulator.IsGenerating;
                if (!ready) readySince = -1;
                else if (readySince < 0) readySince = EditorApplication.timeSinceStartup;
                else if (EditorApplication.timeSinceStartup - readySince > 1.5) return true;
                await Task.Delay(250, cancel);
            }
            Fail("The park never finished loading (no main camera, or content stayed parked).");
            return false;
        }

        static async Task RunLoop(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                if (Current.freezeTime) PlaytestClock.Freeze();
                PruneScreenshots();
                AddEntry("system", "Thinking…", transient: true);

                JObject response;
                try
                {
                    response = await _client.CreateMessage(BuildRequest(), cancel);
                }
                catch (PlaytestApiException e)
                {
                    Fail(e.Message);
                    return;
                }
                cancel.ThrowIfCancellationRequested();
                RemoveTransient();

                var content = response["content"] as JArray ?? new JArray();
                string stop = response.Value<string>("stop_reason");
                if (stop == "refusal")
                {
                    Fail("Claude declined to continue: " + (response["stop_details"]?.Value<string>("explanation") ?? "no explanation"));
                    return;
                }

                // Echo the assistant turn back verbatim (thinking blocks included).
                Current.messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                foreach (var block in content.OfType<JObject>())
                {
                    string type = block.Value<string>("type");
                    if (type == "text" && !string.IsNullOrWhiteSpace(block.Value<string>("text"))) AddEntry("say", block.Value<string>("text").Trim());
                    else if (type == "thinking" && !string.IsNullOrWhiteSpace(block.Value<string>("thinking"))) AddEntry("thinking", block.Value<string>("thinking").Trim());
                }

                // A restart tears the world down, so it always runs after its siblings.
                var toolUses = content.OfType<JObject>().Where(b => b.Value<string>("type") == "tool_use")
                    .OrderBy(b => b.Value<string>("name") == "restart_playtest" ? 1 : 0).ToList();
                if (toolUses.Count == 0)
                {
                    if (_finishRequested) break;
                    if (Current.nudges >= 2 || Current.steps >= Current.maxSteps)
                    {
                        Finish("incomplete", LastSay() ?? "The tester stopped without calling finish.");
                        return;
                    }
                    Current.nudges++;
                    string nudge = stop == "max_tokens"
                        ? "Your last response was cut off. Continue with a tool call."
                        : "Keep playtesting with the tools, or call finish if you are done.";
                    Current.messages.Add(new JObject { ["role"] = "user", ["content"] = nudge });
                    Save();
                    continue;
                }

                var results = new JArray();
                foreach (var use in toolUses)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = use.Value<string>("name");
                    var input = use["input"] as JObject ?? new JObject();
                    string id = use.Value<string>("id");
                    Current.steps++;
                    AddEntry("action", $"{name} {Compact(input)}");

                    if (name == "restart_playtest")
                    {
                        BeginRestart(id, results, input.Value<string>("reason"));
                        return;
                    }

                    PlaytestToolOutput output = await ExecuteTool(name, input);
                    results.Add(ToolResult(id, output));
                    AddEntry(output.isError ? "error" : "result", FirstLine(output.text, 400), SaveShot(output.jpeg));
                }

                if (Current.steps >= Current.maxSteps && !_finishRequested && !Current.budgetWarned)
                {
                    results.Add(new JObject { ["type"] = "text", ["text"] = "Step budget reached. Call finish now with your verdict and summary." });
                    Current.budgetWarned = true;
                }
                Current.messages.Add(new JObject { ["role"] = "user", ["content"] = results });
                Save();

                if (_finishRequested)
                {
                    Finish(Current.verdict, Current.summary);
                    return;
                }
                if (Current.steps >= Current.maxSteps + 3)
                {
                    Finish("incomplete", "Step budget exhausted.");
                    return;
                }
            }
        }

        static async Task<PlaytestToolOutput> ExecuteTool(string name, JObject input)
        {
            switch (name)
            {
                case "report_issue":
                {
                    var issue = new Issue
                    {
                        severity = input.Value<string>("severity") ?? "major",
                        title = input.Value<string>("title") ?? "(untitled)",
                        what_happened = input.Value<string>("what_happened"),
                        expected = input.Value<string>("expected"),
                        evidence = input.Value<string>("evidence"),
                        suggested_fix = input.Value<string>("suggested_fix"),
                        script = input.Value<string>("script"),
                    };
                    byte[] shot = PlaytestObserver.CaptureHead();
                    if (shot != null)
                    {
                        issue.screenshot = $"{Current.runDir}/issue-{Current.issues.Count + 1}.jpg";
                        File.WriteAllBytes(issue.screenshot, shot);
                    }
                    Current.issues.Add(issue);
                    AddEntry("issue", $"[{issue.severity}] {issue.title}", issue.screenshot);
                    return new PlaytestToolOutput { text = $"Logged issue #{Current.issues.Count}." };
                }
                case "read_script":
                {
                    string src = PlaytestBriefing.ReadScript(input.Value<string>("name"));
                    return src != null ? new PlaytestToolOutput { text = src } : PlaytestToolOutput.Error("No Lua script with that name.");
                }
                case "apply_fix":
                    return ApplyFix(input);
                case "finish":
                    _finishRequested = true;
                    Current.verdict = input.Value<string>("verdict") ?? "issues_found";
                    Current.summary = input.Value<string>("summary") ?? "";
                    return new PlaytestToolOutput { text = "Playtest finished. Writing the report." };
                default:
                    return await _tools.Execute(name, input);
            }
        }

        // ── Fix + restart ─────────────────────────────────────────────────

        static PlaytestToolOutput ApplyFix(JObject input)
        {
            if (!Current.allowFixes) return PlaytestToolOutput.Error("Fixes are disabled for this run. Use report_issue with suggested_fix.");
            string path = (input.Value<string>("path") ?? "").Replace('\\', '/');
            string folder = ContentFolders.Root + "/" + Current.contentId + "/";
            if (!path.StartsWith(folder) || !path.EndsWith(".lua.txt") || path.Contains("..") || path.Contains("/ThirdParty"))
                return PlaytestToolOutput.Error($"Only .lua.txt files under {folder} (outside ThirdParty) may be edited.");
            if (!File.Exists(path)) return PlaytestToolOutput.Error("File not found: " + path);

            string oldText = input.Value<string>("old_string") ?? "";
            string newText = input.Value<string>("new_string") ?? "";
            string source = File.ReadAllText(path);
            int first = string.IsNullOrEmpty(oldText) ? -1 : source.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0) return PlaytestToolOutput.Error("old_string was not found in the file. Read the script again and copy it exactly.");
            if (source.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
                return PlaytestToolOutput.Error("old_string appears more than once. Include more surrounding lines so it is unique.");

            string backup = $"{Current.runDir}/fix-{Current.fixes.Count + 1}-{Path.GetFileName(path)}.orig";
            File.WriteAllText(backup, source);
            File.WriteAllText(path, source.Substring(0, first) + newText + source.Substring(first + oldText.Length));
            AssetDatabase.ImportAsset(path);

            var fix = new Fix { path = path, rationale = input.Value<string>("rationale"), old_string = oldText, new_string = newText, backup = backup };
            Current.fixes.Add(fix);
            AddEntry("fix", $"{path}: {fix.rationale}");
            return new PlaytestToolOutput { text = $"Applied to {path} (original saved to {backup}). Scripts that already booted still run the old code: call restart_playtest to test the fix." };
        }

        static void BeginRestart(string toolUseId, JArray priorResults, string reason)
        {
            Current.restarts++;
            Current.pendingRestartToolUseId = toolUseId;
            Current.pendingResults = priorResults;
            Current.status = Status.Restarting;
            AddEntry("system", "Restarting Play mode" + (string.IsNullOrEmpty(reason) ? "…" : $": {reason}"));
            PlaytestClock.Thaw();
            Save();
            EditorApplication.isPlaying = false;
        }

        static JObject ResumeAfterRestart()
        {
            var results = Current.pendingResults ?? new JArray();
            string snapshot = PlaytestObserver.Snapshot(PlaytestAvatar.Instance);
            var output = new PlaytestToolOutput
            {
                text = "Play mode restarted; the game is fresh and your fixes are loaded. You are back at the start position.\n" + snapshot,
                jpeg = PlaytestObserver.CaptureHead(),
            };
            results.Add(ToolResult(Current.pendingRestartToolUseId, output));
            AddEntry("result", "Restarted.", SaveShot(output.jpeg));
            Current.pendingRestartToolUseId = null;
            Current.pendingResults = null;
            return new JObject { ["role"] = "user", ["content"] = results };
        }

        // ── Messages ──────────────────────────────────────────────────────

        static JObject FirstUserMessage()
        {
            string goal = string.IsNullOrWhiteSpace(Current.goal)
                ? "Play the whole game the way a first-time guest would, find everything that is broken or confusing, and report it."
                : Current.goal.Trim();
            var content = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = PlaytestBriefing.Build(Current.contentId) },
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = $"Content package: {Current.contentId}\nYour playtest goal from the creator: {goal}\n\n" +
                               "Here is where you are standing right now:\n" + PlaytestObserver.Snapshot(PlaytestAvatar.Instance),
                },
            };
            byte[] shot = PlaytestObserver.CaptureHead();
            if (shot != null)
            {
                content.Add(ImageBlock(shot));
                AddEntry("result", "Initial view.", SaveShot(shot));
            }
            return new JObject { ["role"] = "user", ["content"] = content };
        }

        static JObject BuildRequest()
        {
            var tools = PlaytestTools.Definitions();
            foreach (var t in SessionTools(Current.allowFixes)) tools.Add(t);
            return new JObject
            {
                ["model"] = Current.model,
                ["max_tokens"] = 16000,
                ["system"] = new JArray { new JObject { ["type"] = "text", ["text"] = SystemPrompt(Current.allowFixes) } },
                ["tools"] = tools,
                ["messages"] = Current.messages,
                ["thinking"] = new JObject { ["type"] = "adaptive", ["display"] = "summarized" },
                ["output_config"] = new JObject { ["effort"] = Current.effort },
                // Automatic prompt caching: the briefing, tools and history are a
                // stable, growing prefix, so each turn only pays for what's new.
                ["cache_control"] = new JObject { ["type"] = "ephemeral" },
            };
        }

        static JObject ToolResult(string id, PlaytestToolOutput output)
        {
            var content = new JArray { new JObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(output.text) ? "(no output)" : output.text } };
            if (output.jpeg != null) content.Add(ImageBlock(output.jpeg));
            var result = new JObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content };
            if (output.isError) result["is_error"] = true;
            return result;
        }

        static JObject ImageBlock(byte[] jpeg) => new JObject
        {
            ["type"] = "image",
            ["source"] = new JObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpeg) },
        };

        /// Old screenshots are the bulk of the context. Once there are many, drop
        /// all but the newest few in one pass (batched so the cached prefix is only
        /// rewritten occasionally, not every turn).
        static void PruneScreenshots()
        {
            var images = Current.messages.SelectTokens("$..[?(@.type == 'image')]").OfType<JObject>().ToList();
            if (images.Count < PruneAtScreenshots) return;
            foreach (var img in images.Take(images.Count - KeepScreenshots))
                img.Replace(new JObject { ["type"] = "text", ["text"] = "[older screenshot removed to save context]" });
        }

        static string SystemPrompt(bool allowFixes)
        {
            var sb = new StringBuilder(@"You are the DreamPark AI playtester. You play a creator's mixed-reality game inside the Unity Editor by controlling a simulated guest (head, two hands, body), then report what works and what is broken, with evidence.

How DreamPark works:
- A guest wearing a Quest headset walks around a real room with virtual attractions overlaid on it. Physical movement is the only locomotion: walking, turning, crouching, reaching. There are no controllers, buttons, teleports or keyboards.
- Interaction is collider-based. Content reacts when a hand or the body enters a trigger or collides with something (Lua ontriggerenter / oncollisionenter checking dp.is_player). To press something, touch it. To hit something, strike it. To use a floor pad, step_on it. Walking into an attraction's footprint is what activates it (onzoneenter).
- Many games count a touch only through colliders the game put on your rig (a coin collector on a hand, a damage capsule on the body). PLAYER SYSTEMS in observe lists them and says which hand each one rides on. Reach with that hand, or the game won't notice.
- Gameplay is Lua (LuaBehaviour). Script globals show up as ""state"" and ""fns"" in observe/inspect, and lua_eval can read them. Script locals are not visible.
- Virtual geometry never blocks walking.
- You may start far from everything (the Editor spawns you wherever its camera was). If observe says NO FLOOR UNDER YOU or nothing is nearby, travel_to the nearest attraction first.
- Game time is frozen while you think and runs only during your actions, so take the time you need.

How to test:
1. Read the briefing. Designer notes are the spec; otherwise infer the intended loop from the prefabs and scripts. Before acting, say in one to three sentences how you think the game is meant to be played and what success looks like.
2. Play it like a first-time guest: approach, look, interact, and follow the loop to its goal. Then probe the edges: repeat an interaction quickly, leave mid-game and come back, reach awkward spots, wait out timers.
3. Verify with evidence, not assumptions. Use the EVENTS after each action (errors, contacts, zone changes, storage writes), state in observe/inspect, lua_eval on getters, world text, and screenshots. If a contact happened but nothing changed, that is a finding.
4. Report each distinct problem once, with report_issue, as soon as you have confirmed it. Examples: Lua errors; interactions that don't respond; score or state not updating; softlocks; objects floating, sunk into the floor or stacked at the origin; things out of a guest's reach; missing or confusing feedback; anything that contradicts the designer notes. Say what you did, what happened, what should have happened, and the evidence (log lines, state values). Name the script and function when you can, and propose a concrete fix.
5. Do not report these as bugs: use of EasyEvent components (deprecated but supported), editor-only artifacts (no passthrough, grey background), or limits of your simulated body. If something only fails because of how you are simulated, say so explicitly.
6. When you have covered the core loop and the edge cases, or you are blocked, call finish with a verdict and a short summary written for the creator.

Keep narration brief: one short sentence of intent before a group of actions.");
            if (allowFixes)
            {
                sb.Append(@"

Fixing: you may fix bugs in the creator's Lua with apply_fix. Only .lua.txt files under the content folder can be edited. File a report_issue first, then make the smallest change that fixes it, in the file's existing style. Never remove gameplay you don't understand. After fixing, call restart_playtest to reload the game, then re-verify. If the fix didn't work, say so and try again or revert it.");
            }
            return sb.ToString();
        }

        internal static IEnumerable<JObject> SessionTools(bool allowFixes)
        {
            yield return JObject.Parse(@"{""name"":""report_issue"",""description"":""Record one confirmed problem for the creator. A screenshot of your current view is attached automatically."",
""input_schema"":{""type"":""object"",""properties"":{
""severity"":{""type"":""string"",""enum"":[""blocker"",""major"",""minor"",""polish""]},
""title"":{""type"":""string""},""what_happened"":{""type"":""string""},""expected"":{""type"":""string""},
""evidence"":{""type"":""string"",""description"":""Log lines, state values, contacts that prove it.""},
""suggested_fix"":{""type"":""string""},""script"":{""type"":""string"",""description"":""Script path/name and function, if known.""}},
""required"":[""severity"",""title"",""what_happened"",""expected"",""evidence""]}}");
            yield return JObject.Parse(@"{""name"":""read_script"",""description"":""Read a Lua script's full source by file name or Assets/ path."",
""input_schema"":{""type"":""object"",""properties"":{""name"":{""type"":""string""}},""required"":[""name""]}}");
            yield return JObject.Parse(@"{""name"":""finish"",""description"":""End the playtest with a verdict and a summary for the creator."",
""input_schema"":{""type"":""object"",""properties"":{""verdict"":{""type"":""string"",""enum"":[""pass"",""issues_found"",""blocked""]},""summary"":{""type"":""string""}},""required"":[""verdict"",""summary""]}}");
            yield return JObject.Parse(@"{""name"":""restart_playtest"",""description"":""Exit and re-enter Play mode so the game starts fresh (and edited scripts reload). You return to the start position."",
""input_schema"":{""type"":""object"",""properties"":{""reason"":{""type"":""string""}}}}");
            if (allowFixes)
            {
                yield return JObject.Parse(@"{""name"":""apply_fix"",""description"":""Replace one exact, unique snippet in a creator Lua script (.lua.txt under the content folder). The original is backed up."",
""input_schema"":{""type"":""object"",""properties"":{""path"":{""type"":""string""},""old_string"":{""type"":""string""},""new_string"":{""type"":""string""},""rationale"":{""type"":""string""}},""required"":[""path"",""old_string"",""new_string"",""rationale""]}}");
            }
        }

        // ── Ending ────────────────────────────────────────────────────────

        static void Finish(string verdict, string summary)
        {
            Current.status = Status.Finished;
            Current.verdict = verdict;
            Current.summary = summary;
            AddEntry("system", $"Finished: {verdict}.");
            TearDownWorld();
            WriteReport();
            Save();
        }

        static void Fail(string message)
        {
            if (!IsActive && Current.status != Status.Idle) return;
            Current.status = Status.Failed;
            Current.error = message;
            AddEntry("error", message);
            TearDownWorld();
            WriteReport();
            Save();
        }

        static void TearDownWorld()
        {
            RemoveTransient();
            PlaytestClock.Thaw();
            if (Application.isPlaying) PlaytestAvatar.Despawn();
            PlaytestObserver.End();
        }

        static void WriteReport()
        {
            if (string.IsNullOrEmpty(Current.runDir)) return;
            Directory.CreateDirectory(Current.runDir);
            var sb = new StringBuilder();
            sb.Append($"# Playtest report — {Current.contentId}\n\n");
            sb.Append($"- **Verdict:** {Current.verdict ?? Current.status.ToString()}\n");
            sb.Append($"- **Model:** {Current.model} (effort {Current.effort})\n");
            sb.Append($"- **Started:** {Current.startedUtc}  **Steps:** {Current.steps}  **Restarts:** {Current.restarts}\n");
            if (!string.IsNullOrEmpty(Current.goal)) sb.Append($"- **Goal:** {Current.goal}\n");
            if (!string.IsNullOrEmpty(Current.error)) sb.Append($"- **Stopped:** {Current.error}\n");
            sb.Append('\n');
            if (!string.IsNullOrEmpty(Current.summary)) sb.Append("## Summary\n\n").Append(Current.summary).Append("\n\n");

            sb.Append($"## Issues ({Current.issues.Count})\n\n");
            if (Current.issues.Count == 0) sb.Append("None reported.\n\n");
            int n = 0;
            foreach (var i in Current.issues.OrderBy(i => SeverityRank(i.severity)))
            {
                sb.Append($"### {++n}. [{i.severity}] {i.title}\n\n");
                if (!string.IsNullOrEmpty(i.what_happened)) sb.Append($"**What happened:** {i.what_happened}\n\n");
                if (!string.IsNullOrEmpty(i.expected)) sb.Append($"**Expected:** {i.expected}\n\n");
                if (!string.IsNullOrEmpty(i.evidence)) sb.Append($"**Evidence:** {i.evidence}\n\n");
                if (!string.IsNullOrEmpty(i.script)) sb.Append($"**Where:** `{i.script}`\n\n");
                if (!string.IsNullOrEmpty(i.suggested_fix)) sb.Append($"**Suggested fix:** {i.suggested_fix}\n\n");
                if (!string.IsNullOrEmpty(i.screenshot)) sb.Append($"![]({Path.GetFileName(i.screenshot)})\n\n");
            }

            if (Current.fixes.Count > 0)
            {
                sb.Append($"## Fixes applied ({Current.fixes.Count})\n\nOriginals are saved next to this report. Review before committing.\n\n");
                foreach (var f in Current.fixes)
                {
                    sb.Append($"- `{f.path}` — {f.rationale}\n\n```diff\n");
                    foreach (var l in f.old_string.Split('\n')) sb.Append("- ").Append(l).Append('\n');
                    foreach (var l in f.new_string.Split('\n')) sb.Append("+ ").Append(l).Append('\n');
                    sb.Append("```\n\n");
                }
            }

            var errors = PlaytestObserver.All().Where(e => e.kind.Contains("error")).ToList();
            if (errors.Count > 0)
            {
                sb.Append("## Errors logged during the run\n\n");
                foreach (var e in errors.Take(40)) sb.Append($"- `{e.text.Replace("`", "'")}`{(e.count > 1 ? $" (x{e.count})" : "")}\n");
                sb.Append('\n');
            }

            sb.Append("## Play log\n\n");
            foreach (var e in Current.entries.Where(e => e.kind == "say" || e.kind == "action" || e.kind == "issue" || e.kind == "fix" || e.kind == "error"))
                sb.Append($"- **{e.kind}:** {e.text.Replace("\n", " ")}\n");

            Current.reportPath = Current.runDir + "/report.md";
            File.WriteAllText(Current.reportPath, sb.ToString());
        }

        static int SeverityRank(string s) => s == "blocker" ? 0 : s == "major" ? 1 : s == "minor" ? 2 : 3;

        // ── Bookkeeping ───────────────────────────────────────────────────

        static void AddEntry(string kind, string text, string image = null, bool transient = false)
        {
            Current.entries.Add(new Entry { kind = transient ? "transient" : kind, text = text, image = image });
            if (Current.entries.Count > MaxEntries) Current.entries.RemoveRange(0, Current.entries.Count - MaxEntries);
            if (kind == "issue" || kind == "error") Debug.Log($"[Playtest] {kind}: {text}");
            Changed?.Invoke();
        }

        static void RemoveTransient()
        {
            if (Current.entries.RemoveAll(e => e.kind == "transient") > 0) Changed?.Invoke();
        }

        static string SaveShot(byte[] jpeg)
        {
            if (jpeg == null || string.IsNullOrEmpty(Current.runDir)) return null;
            LastScreenshot = jpeg;
            string path = $"{Current.runDir}/shot-{++Current.shots:000}.jpg";
            File.WriteAllBytes(path, jpeg);
            return path;
        }

        static string LastSay() => Current.entries.LastOrDefault(e => e.kind == "say")?.text;

        static string Compact(JObject input)
        {
            string s = input.ToString(Formatting.None);
            return s.Length > 240 ? s.Substring(0, 240) + "…" : s;
        }

        static string FirstLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(Current));
            }
            catch (Exception e) { Debug.LogWarning("[Playtest] Could not save session: " + e.Message); }
            Changed?.Invoke();
        }

        static void Load()
        {
            try
            {
                if (File.Exists(StatePath)) Current = JsonConvert.DeserializeObject<State>(File.ReadAllText(StatePath)) ?? new State();
                string last = Current.entries?.LastOrDefault(e => !string.IsNullOrEmpty(e.image))?.image;
                if (last != null && File.Exists(last)) LastScreenshot = File.ReadAllBytes(last);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Playtest] Could not restore session: " + e.Message);
                Current = new State();
            }
        }
    }
}
#endif
