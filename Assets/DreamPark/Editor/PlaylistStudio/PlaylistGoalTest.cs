#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Defective.JSON;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// Playlist Studio "Test" (repair Oct 20 2026): runs ONE goal block in the real XLua VM (a fresh LuaEnv) against fake
    /// events, with the same harness the agents' <c>Tools/agent/goaltest.py</c> uses
    /// (GoalTest/goal-test-harness.lua.txt: a fake stop/dp per CONTRACT § 2, the allow-listed globals only, default
    /// events per challenge kind). No scene, no play mode.
    /// </summary>
    public static class PlaylistGoalTest
    {
        public const string HarnessAsset = "Assets/DreamPark/Editor/PlaylistStudio/GoalTest/goal-test-harness.lua.txt";

        public sealed class Result
        {
            public bool ok, settled;
            public string status = "", error, json;
            public double at;
            public string lastProgress = "";
            public List<string> lines = new List<string>();
            public string Summary => error != null ? "✕ " + error
                : (settled ? "✓ " : "… ") + (string.IsNullOrEmpty(status) ? "never settled" : status) + $" at {at:0.#} s"
                  + (lastProgress.Length > 0 ? " · progress " + lastProgress : "") + $" · {lines.Count} log lines";
        }

        public static string HarnessPath()
        {
            // the SDK ships it at the same asset path in every project; fall back to a search (moved SDK folders)
            string p = Path.GetFullPath(Path.Combine(Application.dataPath, "..", HarnessAsset));
            if (File.Exists(p)) return p;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("goal-test-harness.lua"))
            {
                string ap = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (ap.EndsWith("goal-test-harness.lua.txt", StringComparison.Ordinal)) return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ap));
            }
            return p;
        }

        /// stepsJson: a JSON array of steps (see the harness header) or null for the kind's default fake events.
        public static Result Run(string block, string kind, string stepsJson = null, string optionsJson = null, string attraction = "A_Test", double untilSec = 900)
        {
            var r = new Result();
            string harness;
            try { harness = File.ReadAllText(HarnessPath()); }
            catch (Exception e) { r.error = "harness not found: " + e.Message; return r; }
            var args = new StringBuilder("{ block = ").Append(LuaString(block ?? "")).Append(", kind = ").Append(LuaString(kind ?? ""))
                .Append(", until_sec = ").Append(untilSec.ToString(CultureInfo.InvariantCulture))
                .Append(", stop = { id = 'stop-1', attraction = ").Append(LuaString(attraction ?? "A_Test"));
            JSONObject options = Parse(optionsJson);
            if (options != null && options.type == JSONObject.Type.Object) args.Append(", options = ").Append(ToLua(options));
            args.Append(" }");
            JSONObject steps = Parse(stepsJson);
            if (steps != null && steps.type == JSONObject.Type.Array) args.Append(", steps = ").Append(ToLua(steps));
            args.Append(" }");
            string src = "local H = (function()\n" + harness + "\nend)()\nreturn H.run(" + args + ")\n";
            XLua.LuaEnv env = null;
            try
            {
                env = new XLua.LuaEnv();
                object[] outv = env.DoString(src, "goaltest");
                r.json = outv != null && outv.Length > 0 ? outv[0] as string : null;
            }
            catch (Exception e) { r.error = e.Message; return r; }
            finally { try { env?.Dispose(); } catch { } }
            Read(r);
            return r;
        }

        /// Fill a Result from the harness JSON (also used by the offline tests).
        public static void Read(Result r)
        {
            JSONObject j = Parse(r.json);
            if (j == null) { r.error = r.error ?? "the harness returned nothing"; return; }
            r.ok = j.GetField("ok")?.boolValue == true;
            if (j.GetField("error") != null && j.GetField("error").type == JSONObject.Type.String) r.error = j.GetField("error").stringValue;
            JSONObject res = j.GetField("result");
            if (res != null && res.type == JSONObject.Type.Object)
            {
                r.status = res.GetField("status")?.stringValue ?? "";
                r.at = res.GetField("at") != null ? res.GetField("at").doubleValue : 0;
            }
            r.settled = r.ok && (r.status == "done" || r.status == "failed" || r.status == "timed_out" || r.status == "skip");
            foreach (JSONObject e in j.GetField("log")?.list ?? new List<JSONObject>())
            {
                string what = e.GetField("what")?.stringValue ?? "?";
                double at = e.GetField("at") != null ? e.GetField("at").doubleValue : 0;
                string detail = "";
                if (what == "progress") { detail = $"{Num(e, "n")}/{Num(e, "of")} {e.GetField("unit")?.stringValue}"; r.lastProgress = $"{Num(e, "n")}/{Num(e, "of")}"; }
                else if (what == "event") detail = $"{e.GetField("source")?.stringValue} {e.GetField("name")?.stringValue} → {Num(e, "handlers")} handler(s)";
                else if (what == "error") detail = e.GetField("message")?.stringValue ?? "";
                else if (what == "dreamie.moment") detail = e.GetField("name")?.stringValue ?? "";
                else if (what == "mark") detail = e.GetField("mark")?.Print() ?? "";
                else if (what == "jump" || what == "crossing" || what == "reach") detail = e.GetField("spec")?.Print() ?? "";
                else if (what == "done" || what == "failed" || what == "skip") detail = e.GetField("fields")?.Print() ?? "";
                else if (what == "census") detail = $"{e.GetField("q")?.Print()} → {Num(e, "n")}";
                r.lines.Add($"{at,6:0.0}s  {what,-15} {detail}");
            }
        }

        static string Num(JSONObject e, string k)
        {
            JSONObject f = e.GetField(k);
            if (f == null) return "?";
            return f.isInteger ? f.longValue.ToString(CultureInfo.InvariantCulture) : f.doubleValue.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static JSONObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return new JSONObject(json); } catch { return null; }
        }

        /// A Lua long-bracket string that cannot be closed by its content.
        public static string LuaString(string s)
        {
            int level = 0;
            while (s.Contains("]" + new string('=', level) + "]")) level++;
            string eq = new string('=', level);
            return "[" + eq + "[\n" + s + "]" + eq + "]";
        }

        /// JSON → a Lua table constructor.
        public static string ToLua(JSONObject o)
        {
            if (o == null) return "nil";
            switch (o.type)
            {
                case JSONObject.Type.Object:
                    return "{" + string.Join(", ", Enumerable.Range(0, o.count).Select(i => "[" + LuaString(o.keys[i]) + "] = " + ToLua(o.list[i]))) + "}";
                case JSONObject.Type.Array:
                    return "{" + string.Join(", ", (o.list ?? new List<JSONObject>()).Select(ToLua)) + "}";
                case JSONObject.Type.String: return LuaString(o.stringValue ?? "");
                case JSONObject.Type.Number: return o.isInteger ? o.longValue.ToString(CultureInfo.InvariantCulture) : o.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case JSONObject.Type.Bool: return o.boolValue ? "true" : "false";
                default: return "nil";
            }
        }

        /// Compile only (syntax) in the XLua VM: null when it compiles, else the error.
        public static string Compile(string lua, string name = "goal")
        {
            XLua.LuaEnv env = null;
            try
            {
                env = new XLua.LuaEnv();
                env.DoString("local f, e = load(" + LuaString(lua ?? "") + ", '=" + name + "', 't', {}) if not f then error(e, 0) end", "compile");
                return null;
            }
            catch (Exception e) { return e.Message; }
            finally { try { env?.Dispose(); } catch { } }
        }
    }
}
#endif
