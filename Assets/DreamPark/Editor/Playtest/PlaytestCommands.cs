#if UNITY_EDITOR && DREAMPARK_SDK_PACKAGES_READY
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DreamPark.ParkBuilder;
using DreamPark.ParkSim;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Playtest
{
    /// <summary>
    /// Unity CLI surface for the AI playtester, so a coding agent (Claude Code
    /// driving `unity command`) can build content, play it, see what happened, fix
    /// the Lua and play again — the same tools the in-Editor tester uses.
    ///
    ///   unity command editor_play
    ///   unity command dreampark_playtest_attach
    ///   unity command dreampark_playtest_act --tool reach --args '{"hand":"right","target":"e4","style":"strike"}'
    ///   unity command dreampark_playtest_detach
    ///
    /// Or hand the whole run to the built-in tester:
    ///   unity command dreampark_playtest_run --goal "Beat the whack-a-mole"
    ///   unity command dreampark_playtest_status
    /// </summary>
    public static class PlaytestCommands
    {
        const string ShotDir = "Logs/DreamParkPlaytest/cli";
        static PlaytestTools _tools;
        static int _shots;

        [CliCommand("dreampark_playtest_attach", "Spawn the AI-playtest avatar into the running game (Play mode must be on; use editor_play first). It takes over the player's head and hands from the Scene-view Simulator. Returns a world snapshot and a screenshot path.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static async Task<object> Attach(
            [CliArg("body_probe", "Add a floor-to-eye body collider under the PlayerRig so floor pads and walk-in triggers fire.")] bool bodyProbe = true,
            [CliArg("freeze_time", "Freeze game time between actions so thinking time doesn't count.")] bool freezeTime = true,
            [CliArg("auto_screenshot", "Save a head-camera screenshot after every action.")] bool autoScreenshot = true)
        {
            if (!EditorApplication.isPlaying) return new { success = false, error = "Not in Play mode. Run editor_play, then attach." };
            if (PlaytestSession.IsActive) return new { success = false, error = "The built-in AI playtester is running. Stop it first (dreampark_playtest_stop)." };

            double deadline = EditorApplication.timeSinceStartup + 90;
            while (!(Camera.main != null && LevelObjectManager.objectsEnabled && !ParkSimulator.IsGenerating))
            {
                if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup > deadline)
                    return new { success = false, error = "The park did not finish loading." };
                await Task.Delay(250);
            }

            PlaytestObserver.Begin();
            PlaytestAvatar.Spawn(bodyProbe);
            _tools = new PlaytestTools(freezeTime, autoScreenshot);
            if (freezeTime) PlaytestClock.Freeze();
            return new
            {
                success = true,
                snapshot = PlaytestObserver.Snapshot(PlaytestAvatar.Instance),
                screenshot = SaveShot(PlaytestObserver.CaptureHead()),
                tools = "Run dreampark_playtest_tools for the action schemas.",
            };
        }

        [CliCommand("dreampark_playtest_tools", "List the playtest actions (JSON schemas) accepted by dreampark_playtest_act.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static object Tools()
        {
            return PlaytestTools.Definitions().Select(t => new
            {
                name = t.Value<string>("name"),
                description = t.Value<string>("description"),
                input_schema = t["input_schema"].ToString(Newtonsoft.Json.Formatting.None),
            }).ToArray();
        }

        [CliCommand("dreampark_playtest_act", "Perform one playtest action (observe, screenshot, walk_to, step_on, look_at, turn, reach, retract, crouch, wait, inspect, lua_eval). Game time runs only during the action. Returns the outcome, the events it caused (errors, contacts, zone and storage changes) and a screenshot path.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static async Task<object> Act(
            [CliArg("tool", "Action name; see dreampark_playtest_tools.", Required = true)] string tool,
            [CliArg("args", "Action arguments as a JSON object, e.g. '{\"target\":\"e3\"}'.")] string args = "{}")
        {
            if (_tools == null || PlaytestAvatar.Instance == null)
                return new { success = false, error = "No avatar. Run dreampark_playtest_attach first (Play mode resets it)." };
            JObject input;
            try { input = string.IsNullOrWhiteSpace(args) ? new JObject() : JObject.Parse(args); }
            catch (Exception e) { return new { success = false, error = "args is not a JSON object: " + e.Message }; }

            var output = await _tools.Execute(tool, input);
            return new { success = !output.isError, result = output.text, screenshot = SaveShot(output.jpeg) };
        }

        [CliCommand("dreampark_playtest_detach", "Remove the playtest avatar and hand the player back to the Scene-view Simulator.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static object Detach()
        {
            PlaytestClock.Thaw();
            if (EditorApplication.isPlaying) PlaytestAvatar.Despawn();
            PlaytestObserver.End();
            _tools = null;
            return new { success = true };
        }

        [CliCommand("dreampark_playtest_run", "Start the built-in AI playtester (Claude plays the game, reports issues, writes a report). Needs ANTHROPIC_API_KEY in the Editor's environment or a key saved in DreamPark → AI Playtester. Enters Play mode if needed and returns immediately; poll dreampark_playtest_status.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static object Run(
            [CliArg("goal", "What to test. Empty = play the whole game and report everything.")] string goal = "",
            [CliArg("content_id", "Content folder under Assets/Content. Defaults to the creator's game.")] string contentId = "",
            [CliArg("max_steps", "Tool-call budget.")] int maxSteps = 60,
            [CliArg("allow_fixes", "Let the tester patch .lua.txt files in the content folder and restart Play mode to re-verify.")] bool allowFixes = false,
            [CliArg("model", "claude-opus-5 or claude-sonnet-5.")] string model = "",
            [CliArg("base_url", "Messages API base URL for this run only (a proxy; the API key is then optional). Default: the window setting, else api.anthropic.com.")] string baseUrl = "")
        {
            var o = PlaytestSession.Options.FromSettings();
            o.goal = goal ?? "";
            if (!string.IsNullOrEmpty(contentId)) o.contentId = contentId;
            if (!string.IsNullOrEmpty(model)) o.model = model;
            if (!string.IsNullOrEmpty(baseUrl)) o.baseUrl = baseUrl;
            o.maxSteps = maxSteps;
            o.allowFixes = allowFixes;
            if (_tools != null) Detach();
            string error = PlaytestSession.Start(o);
            return error == null
                ? (object)new { success = true, runDir = Path.GetFullPath(PlaytestSession.Current.runDir) }
                : new { success = false, error };
        }

        [CliCommand("dreampark_playtest_status", "Status of the built-in AI playtester: state, steps, issues found, verdict, report path and the latest play-by-play.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static object Status(
            [CliArg("tail", "How many recent play-by-play entries to include.")] int tail = 15)
        {
            var s = PlaytestSession.Current;
            return new
            {
                status = s.status.ToString(),
                active = PlaytestSession.IsActive,
                s.contentId,
                s.steps,
                s.maxSteps,
                s.verdict,
                s.summary,
                s.error,
                report = string.IsNullOrEmpty(s.reportPath) ? null : Path.GetFullPath(s.reportPath),
                issues = s.issues.Select(i => new { i.severity, i.title, i.script, i.suggested_fix }).ToArray(),
                fixes = s.fixes.Select(f => new { f.path, f.rationale }).ToArray(),
                recent = s.entries.Where(e => e.kind != "thinking" && e.kind != "transient")
                    .Skip(Math.Max(0, s.entries.Count - Math.Max(1, tail))).Select(e => $"{e.kind}: {e.text}").ToArray(),
            };
        }

        [CliCommand("dreampark_playtest_stop", "Stop the built-in AI playtester and write its report.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/playtest" })]
        public static object Stop()
        {
            bool was = PlaytestSession.IsActive;
            PlaytestSession.Stop();
            return new { success = true, wasRunning = was, report = PlaytestSession.Current.reportPath };
        }

        static string SaveShot(byte[] jpeg)
        {
            if (jpeg == null) return null;
            Directory.CreateDirectory(ShotDir);
            string path = $"{ShotDir}/shot-{++_shots:000}.jpg";
            File.WriteAllBytes(path, jpeg);
            return Path.GetFullPath(path);
        }
    }
}
#endif
