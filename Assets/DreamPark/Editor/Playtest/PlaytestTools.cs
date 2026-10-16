#if UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DreamPark.Playtest
{
    /// <summary>Output of one playtest tool call, shaped for a Claude tool_result.</summary>
    public class PlaytestToolOutput
    {
        public string text = "";
        public byte[] jpeg;
        public bool isError;

        public static PlaytestToolOutput Error(string message) => new PlaytestToolOutput { text = message, isError = true };
    }

    /// <summary>
    /// Game time only moves while the tester is acting. While the model is thinking
    /// (seconds, sometimes tens of seconds) the world is frozen, so a real-time game
    /// does not punish the bot for latency a human doesn't have — enemies don't
    /// advance, timers don't run out, and what the model saw is still true when it
    /// acts.
    /// </summary>
    public static class PlaytestClock
    {
        static bool _frozen;
        static float _savedScale = 1f;

        public static bool Frozen => _frozen;

        public static void Freeze()
        {
            if (_frozen || !Application.isPlaying) return;
            _savedScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            _frozen = true;
        }

        public static void Thaw()
        {
            if (!_frozen) return;
            if (Application.isPlaying) Time.timeScale = _savedScale;
            AudioListener.pause = false;
            _frozen = false;
        }
    }

    /// <summary>
    /// The playtester's tool surface: every way it can act on or look at the
    /// world. One implementation serves both drivers — the in-Editor Claude loop
    /// (<see cref="PlaytestSession"/>) and external agents calling the Unity CLI
    /// (<see cref="PlaytestCommands"/>) — so a behaviour fixed for one is fixed for
    /// both.
    /// </summary>
    public class PlaytestTools
    {
        public bool freezeBetweenActions = true;
        public bool autoScreenshot = true;
        int _cursor;

        public PlaytestTools(bool freezeBetweenActions, bool autoScreenshot)
        {
            this.freezeBetweenActions = freezeBetweenActions;
            this.autoScreenshot = autoScreenshot;
            _cursor = PlaytestObserver.EventCount;
        }

        /// JSON-schema tool definitions (Claude tool format).
        public static JArray Definitions()
        {
            const string target = "Object id from observe (e.g. e12) or an exact object name.";
            return JArray.Parse(@"[
{""name"":""observe"",""description"":""Describe the world around you: your pose, nearby attractions, interactable objects (with ids, live Lua state and callable functions), scripts on your own rig, and visible world text. Also returns a head-camera screenshot unless screenshot=false. Call this whenever you need fresh ids or have lost track of the scene."",
 ""input_schema"":{""type"":""object"",""properties"":{""screenshot"":{""type"":""boolean""},""radius"":{""type"":""number"",""description"":""Search radius in meters (default 25).""}}}},
{""name"":""screenshot"",""description"":""Capture an image. view=head is what the guest sees. view=overhead is a top-down map centred on you with your facing pointing to the top of the image; use it to understand layout."",
 ""input_schema"":{""type"":""object"",""properties"":{""view"":{""type"":""string"",""enum"":[""head"",""overhead""]},""altitude"":{""type"":""number"",""description"":""Overhead camera height in meters (default 10).""}},""required"":[""view""]}},
{""name"":""walk_to"",""description"":""Walk in a straight line at a normal walking pace. Give a target object, OR world x/z, OR a relative move (forward/right meters). Virtual geometry does not block you (this is mixed reality)."",
 ""input_schema"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""" + target + @"""},""x"":{""type"":""number""},""z"":{""type"":""number""},""forward"":{""type"":""number""},""right"":{""type"":""number""},""stop_distance"":{""type"":""number"",""description"":""Stop this far (m) from a target object. Default 0.6.""}}}},
{""name"":""travel_to"",""description"":""Harness shortcut, not a guest action: instantly relocate to just outside a distant attraction or object, facing it. Use it only to get between things more than ~10 m apart. Walk for anything closer, because walking is what exercises zones and triggers."",
 ""input_schema"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""" + target + @"""}},""required"":[""target""]}},
{""name"":""step_on"",""description"":""Walk onto an object's centre and stand there briefly, e.g. a floor button, pressure pad or portal. Your body collider reaches the floor."",
 ""input_schema"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""" + target + @"""}},""required"":[""target""]}},
{""name"":""look_at"",""description"":""Turn your head toward a target object or world point."",
 ""input_schema"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""" + target + @"""},""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}}}},
{""name"":""turn"",""description"":""Turn in place. Positive degrees turn right. Optionally set head pitch (degrees, positive looks down)."",
 ""input_schema"":{""type"":""object"",""properties"":{""degrees"":{""type"":""number""},""pitch"":{""type"":""number""}},""required"":[""degrees""]}},
{""name"":""reach"",""description"":""Move a hand to an object (its nearest collider surface) or world point, stepping closer and crouching as a guest would. style=touch presses gently and retracts; strike swings through fast and retracts (hitting, whacking, punching); hold leaves the hand there and carries it with you (holding, carrying, blocking). Moving targets are tracked. Reports what the hand touched."",
 ""input_schema"":{""type"":""object"",""properties"":{""hand"":{""type"":""string"",""enum"":[""left"",""right""]},""target"":{""type"":""string"",""description"":""" + target + @"""},""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},""style"":{""type"":""string"",""enum"":[""touch"",""strike"",""hold""]},""hold_seconds"":{""type"":""number""}},""required"":[""hand""]}},
{""name"":""retract"",""description"":""Bring a hand back to its resting position (releases a hold)."",
 ""input_schema"":{""type"":""object"",""properties"":{""hand"":{""type"":""string"",""enum"":[""left"",""right""]}},""required"":[""hand""]}},
{""name"":""crouch"",""description"":""Set your eye height in meters (standing is about 1.6, crouching about 1.0, kneeling about 0.8)."",
 ""input_schema"":{""type"":""object"",""properties"":{""eye_height"":{""type"":""number""}},""required"":[""eye_height""]}},
{""name"":""wait"",""description"":""Stand still and let the game run (0.1–20 s). Use it to watch animations, timers, spawns or enemies."",
 ""input_schema"":{""type"":""object"",""properties"":{""seconds"":{""type"":""number""}},""required"":[""seconds""]}},
{""name"":""inspect"",""description"":""Details of one object: components, colliders (trigger or solid, rigidbody), every Lua script with its full scope state and function names, and children (with ids)."",
 ""input_schema"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""" + target + @"""}},""required"":[""target""]}},
{""name"":""lua_eval"",""description"":""Evaluate Lua inside one object's script scope, like a developer console. Use it to READ state, e.g. get_points() or state. Avoid changing game state unless you are deliberately setting up a test, and say so if you do."",
 ""input_schema"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""" + target + @"""},""code"":{""type"":""string""},""script"":{""type"":""string"",""description"":""Script name when the object has several LuaBehaviours.""}},""required"":[""target"",""code""]}}
]");
        }

        static bool IsPhysical(string tool)
        {
            switch (tool)
            {
                case "walk_to": case "travel_to": case "step_on": case "look_at": case "turn":
                case "reach": case "retract": case "crouch": case "wait":
                    return true;
                default:
                    return false;
            }
        }

        public async Task<PlaytestToolOutput> Execute(string tool, JObject input)
        {
            input = input ?? new JObject();
            var avatar = PlaytestAvatar.Instance;
            if (!Application.isPlaying) return PlaytestToolOutput.Error("Not in Play mode.");
            if (avatar == null) return PlaytestToolOutput.Error("The playtest avatar is not spawned.");

            try
            {
                if (!IsPhysical(tool)) return ExecuteObservation(tool, input, avatar);

                var body = BuildAction(tool, input, avatar, out string error);
                if (body == null) return PlaytestToolOutput.Error(error);

                PlaytestClock.Thaw();
                PlaytestActionResult result;
                try
                {
                    var task = avatar.Run(tool, body);
                    var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(120)));
                    result = winner == task ? task.Result : new PlaytestActionResult();
                    if (winner != task) result.Fail("Action timed out after 120 s.");
                }
                finally
                {
                    if (freezeBetweenActions) PlaytestClock.Freeze();
                }

                var output = new PlaytestToolOutput { isError = !result.ok };
                string head = result.ok ? "done" : "FAILED: " + result.message;
                if (result.notes.Count > 0) head += " — " + string.Join(" ", result.notes);
                output.text = head + "\n" + StatusLine(avatar) + "\nEVENTS:\n" + PlaytestObserver.FormatEvents(PlaytestObserver.Drain(ref _cursor));
                if (autoScreenshot && PlaytestAvatar.Instance != null) output.jpeg = PlaytestObserver.CaptureHead();
                return output;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return PlaytestToolOutput.Error($"{tool} threw: {e.Message}");
            }
        }

        PlaytestToolOutput ExecuteObservation(string tool, JObject input, PlaytestAvatar avatar)
        {
            switch (tool)
            {
                case "observe":
                {
                    float radius = Num(input, "radius") ?? 25f;
                    string text = PlaytestObserver.Snapshot(avatar, radius)
                        + "\nEVENTS since last report:\n" + PlaytestObserver.FormatEvents(PlaytestObserver.Drain(ref _cursor));
                    bool shot = input.Value<bool?>("screenshot") ?? true;
                    return new PlaytestToolOutput { text = text, jpeg = shot ? PlaytestObserver.CaptureHead() : null };
                }
                case "screenshot":
                {
                    string view = input.Value<string>("view") ?? "head";
                    byte[] jpeg = view == "overhead"
                        ? PlaytestObserver.CaptureOverhead(avatar, Mathf.Clamp(Num(input, "altitude") ?? 10f, 2f, 60f))
                        : PlaytestObserver.CaptureHead();
                    if (jpeg == null) return PlaytestToolOutput.Error("Screenshot failed (see Console).");
                    string caption = view == "overhead"
                        ? "Overhead view: you are at the centre; the top of the image is the direction you face."
                        : "Head camera.";
                    return new PlaytestToolOutput { text = caption, jpeg = jpeg };
                }
                case "inspect":
                {
                    var go = PlaytestObserver.Resolve(input.Value<string>("target"), avatar.HeadPosition);
                    if (go == null) return PlaytestToolOutput.Error("No object matches that target. Call observe for current ids.");
                    return new PlaytestToolOutput { text = PlaytestObserver.Inspect(go, avatar) };
                }
                case "lua_eval":
                {
                    var go = PlaytestObserver.Resolve(input.Value<string>("target"), avatar.HeadPosition);
                    if (go == null) return PlaytestToolOutput.Error("No object matches that target. Call observe for current ids.");
                    string result = PlaytestObserver.LuaEval(go, input.Value<string>("code") ?? "", input.Value<string>("script"));
                    string events = PlaytestObserver.FormatEvents(PlaytestObserver.Drain(ref _cursor));
                    return new PlaytestToolOutput { text = result + "\nEVENTS:\n" + events, isError = result.StartsWith("error") };
                }
                default:
                    return PlaytestToolOutput.Error($"Unknown tool '{tool}'.");
            }
        }

        Func<PlaytestActionResult, IEnumerator> BuildAction(string tool, JObject input, PlaytestAvatar a, out string error)
        {
            error = null;
            GameObject go = null;
            string targetName = input.Value<string>("target");
            if (!string.IsNullOrEmpty(targetName))
            {
                go = PlaytestObserver.Resolve(targetName, a.HeadPosition);
                if (go == null) { error = $"No object matches '{targetName}'. Call observe for current ids."; return null; }
            }
            Vector3? point = PointFrom(input);

            switch (tool)
            {
                case "walk_to":
                {
                    Func<Vector3> dest;
                    float stop = Num(input, "stop_distance") ?? 0.6f;
                    if (go != null) dest = Tracker(go);
                    else if (input["x"] != null && input["z"] != null)
                    {
                        var p = new Vector3(Num(input, "x").Value, a.HeadPosition.y, Num(input, "z").Value);
                        dest = () => p; stop = Num(input, "stop_distance") ?? 0f;
                    }
                    else if (input["forward"] != null || input["right"] != null)
                    {
                        var p = a.HeadPosition + a.YawRotation * new Vector3(Num(input, "right") ?? 0f, 0f, Num(input, "forward") ?? 0f);
                        dest = () => p; stop = 0f;
                    }
                    else { error = "walk_to needs target, x/z, or forward/right."; return null; }
                    return r => a.WalkTo(dest, Mathf.Max(0f, stop), r);
                }
                case "travel_to":
                {
                    if (go == null) { error = "travel_to needs a target."; return null; }
                    var b = PlaytestObserver.BoundsOf(go);
                    Vector3 away = a.HeadPosition - b.center;
                    away.y = 0f;
                    if (away.sqrMagnitude < 1e-4f) away = -a.Forward;
                    Vector3 spot = b.center + away.normalized * (Mathf.Max(b.extents.x, b.extents.z) + 1.5f);
                    return r => a.Teleport(spot, b.center, b.min.y);
                }
                case "step_on":
                {
                    if (go == null) { error = "step_on needs a target."; return null; }
                    var dest = Tracker(go);
                    return r => Sequence(a.WalkTo(dest, 0f, r), a.Wait(0.8f));
                }
                case "look_at":
                {
                    Func<Vector3> p = go != null ? Tracker(go) : point.HasValue ? (Func<Vector3>)(() => point.Value) : null;
                    if (p == null) { error = "look_at needs target or x/y/z."; return null; }
                    return r => a.LookAt(p);
                }
                case "turn":
                {
                    float deg = Num(input, "degrees") ?? 0f;
                    float pitch = Num(input, "pitch") ?? a.Pitch;
                    return r => a.TurnTo(a.Yaw + deg, pitch);
                }
                case "reach":
                {
                    var hand = ParseHand(input);
                    var style = ParseStyle(input.Value<string>("style"));
                    float hold = Num(input, "hold_seconds") ?? 0.3f;
                    Func<Vector3> p;
                    if (go != null)
                    {
                        Vector3 last = PlaytestObserver.TouchPoint(go, a.HeadPosition);
                        p = () =>
                        {
                            if (go != null) last = PlaytestObserver.TouchPoint(go, a.HeadPosition + Vector3.down * 0.25f);
                            return last;
                        };
                    }
                    else if (point.HasValue) { var v = point.Value; p = () => v; }
                    else { error = "reach needs target or x/y/z."; return null; }
                    return r => a.Reach(hand, p, style, hold, r);
                }
                case "retract":
                {
                    var hand = ParseHand(input);
                    return r => a.Retract(hand);
                }
                case "crouch":
                {
                    float h = Num(input, "eye_height") ?? 1.6f;
                    return r => a.SetEyeHeight(h);
                }
                case "wait":
                {
                    float s = Mathf.Clamp(Num(input, "seconds") ?? 1f, 0.1f, 20f);
                    return r => a.Wait(s);
                }
            }
            error = $"Unknown tool '{tool}'.";
            return null;
        }

        static IEnumerator Sequence(params IEnumerator[] steps)
        {
            foreach (var s in steps) yield return s;
        }

        /// Follows an object while it exists; holds its last position if destroyed
        /// (a collected coin) so the motion still completes.
        static Func<Vector3> Tracker(GameObject go)
        {
            Vector3 last = PlaytestObserver.BoundsOf(go).center;
            return () =>
            {
                if (go != null) last = PlaytestObserver.BoundsOf(go).center;
                return last;
            };
        }

        static PlaytestHand ParseHand(JObject input) =>
            string.Equals(input.Value<string>("hand"), "left", StringComparison.OrdinalIgnoreCase) ? PlaytestHand.Left : PlaytestHand.Right;

        static PlaytestReachStyle ParseStyle(string s)
        {
            switch ((s ?? "touch").ToLowerInvariant())
            {
                case "strike": return PlaytestReachStyle.Strike;
                case "hold": return PlaytestReachStyle.Hold;
                default: return PlaytestReachStyle.Touch;
            }
        }

        static Vector3? PointFrom(JObject input)
        {
            if (input["x"] == null || input["y"] == null || input["z"] == null) return null;
            return new Vector3(Num(input, "x").Value, Num(input, "y").Value, Num(input, "z").Value);
        }

        static float? Num(JObject input, string key)
        {
            var t = input[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.Value<float>(); }
            catch { return float.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (float?)null; }
        }

        public static string StatusLine(PlaytestAvatar a)
        {
            var zone = GameArea.currentGameArea;
            var p = a.HeadPosition;
            return string.Format(CultureInfo.InvariantCulture,
                "You are at ({0:0.00}, {1:0.00}, {2:0.00}), facing yaw {3:0}°, eye height {4:0.00} m, attraction: {5}.",
                p.x, p.y, p.z, Mathf.Repeat(a.Yaw, 360f), a.EyeHeight, zone != null ? zone.gameObject.name : "none");
        }
    }
}
#endif
