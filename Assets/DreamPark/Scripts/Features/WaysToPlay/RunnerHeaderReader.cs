// ─────────────────────────────────────────────────────────────────────
//  RunnerHeaderReader.cs — the header of a "way to play" (a runner script).
//
//  A title ships its ways to play as Lua director scripts under
//  Assets/Content/<Title>/Runners/<runnerId>.lua.txt, each opening with
//
//      DreamPark.runner { name = "PvP only", blurb = "…", cover = "Art/pvp.png", default = true }
//
//  The SDK reads that header at upload and exports it into the release
//  (contract ways-to-play.md §3; Docs/RUNNERS.md). Reading = running the file
//  in a THROWAWAY LuaEnv where `dp` (and CS) are inert proxies, so file-scope
//  calls such as dp.flow.onLevelComplete(fn) do nothing and no callback runs.
//  Runtime-safe: no UnityEditor, so the headset can read a header too.
//  (The runtime's DreamPark.RunnerHeader.Parse reads the same header with a
//  regex, without running anything; this one RUNS it, so it also catches
//  syntax errors and computed values — the uploader and checks use this.)
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Text;
using XLua;

namespace DreamPark.WaysToPlay
{
    /// <summary>One way to play, as its header declares it.</summary>
    [Serializable]
    public sealed class RunnerHeaderInfo
    {
        public string runnerId = "";
        public string name = "";
        public string blurb = "";
        /// Path relative to Assets/Content/&lt;Title&gt;/ (or an Assets/... path). "" = none.
        public string cover = "";
        public bool isDefault;
        /// The header call was found and read.
        public bool hasHeader;
        /// Blocking problem (no header, syntax error, bad field). "" = fine.
        public string error = "";
        /// Non-blocking notes (file scope threw AFTER the header, unknown header fields …).
        public List<string> warnings = new List<string>();

        public bool Ok => hasHeader && string.IsNullOrEmpty(error);
        public override string ToString() => $"{runnerId} \"{name}\"{(isDefault ? " (default)" : "")}{(Ok ? "" : " ERROR: " + error)}";
    }

    public static class RunnerIds
    {
        public const string FolderName = "Runners";
        public const string FileSuffix = ".lua.txt";
        public const int MaxLength = 40;

        /// "PvP Only.lua.txt" → "pvp-only". Lower-case, starts with [a-z0-9], then [a-z0-9_-], ≤ 40 chars (server: ^[a-z0-9][a-z0-9_-]{0,63}$).
        public static string FromFileName(string fileName)
        {
            string stem = fileName ?? "";
            int slash = Math.Max(stem.LastIndexOf('/'), stem.LastIndexOf('\\'));
            if (slash >= 0) stem = stem.Substring(slash + 1);
            if (stem.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - FileSuffix.Length);
            else if (stem.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 4);
            var sb = new StringBuilder(stem.Length);
            bool dash = false;
            foreach (char raw in stem.ToLowerInvariant())
            {
                char c = raw;
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) { if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; } continue; }
                sb.Append(c); dash = c == '-';
            }
            string id = sb.ToString().TrimStart('-', '_').TrimEnd('-');
            if (id.Length > MaxLength) id = id.Substring(0, MaxLength).TrimEnd('-');
            return id;
        }

        /// The Addressable address the uploader gives a runner: "{gameId}/Runners/{runnerId}".
        public static string Address(string gameId, string runnerId) => $"{gameId}/{FolderName}/{runnerId}";

        /// RUNNER-ONLY assets (Oct 26 2026): anything under Assets/Content/&lt;Title&gt;/Runners/ — start areas, region maps,
        /// forks, Edge Links. Never a package candidate (Arena / Sequence / Adventure / playlists), so today's app never
        /// offers them; their levels ship in the manifest's runnerLevels instead.
        public static bool IsRunnerOnlyAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string p = assetPath.Replace('\\', '/');
            return p.StartsWith("Assets/Content/", StringComparison.OrdinalIgnoreCase)
                && p.IndexOf("/" + FolderName + "/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// The address of a runner-only level prefab: "{gameId}/Runners/Levels/{prefab name}" — deliberately NOT under
        /// "{gameId}/Levels/", which the live catalog sync files as a placeable attraction.
        public static string LevelAddress(string gameId, string prefabName) => $"{gameId}/{FolderName}/Levels/{prefabName}";
    }

    public static class RunnerHeaderReader
    {
        public const int MaxName = 60;    // ways-web: name 1–60
        public const int MaxBlurb = 200;   // ways-web: blurb ≤ 200

        // dp / CS / any unknown DreamPark.* are inert proxies: indexing, calling and assigning all
        // succeed and do nothing. DreamPark.runner records the header (first call wins, count kept).
        const string Stub = @"
local function inert()
    local p
    local mt = {}
    mt.__index = function() return p end
    mt.__call = function() return p end
    mt.__newindex = function() end
    p = setmetatable({}, mt)
    return p
end
__dp_rh = nil
__dp_rh_count = 0
__dp_rh_after = false
dp = inert()
CS = inert()
DreamPark = setmetatable({
    runner = function(t)
        __dp_rh_count = __dp_rh_count + 1
        if __dp_rh == nil then __dp_rh = t end
    end
}, { __index = function() return inert() end })
";

        /// Read the header of one runner script. Never throws.
        public static RunnerHeaderInfo Read(string luaText, string runnerId)
        {
            var h = new RunnerHeaderInfo { runnerId = runnerId ?? "" };
            if (string.IsNullOrWhiteSpace(luaText)) { h.error = "the script is empty"; return h; }
            LuaEnv env = null;
            try
            {
                env = new LuaEnv();
                env.DoString(Stub, "dp.runner.header_stub");
                string thrown = null;
                try { env.DoString(luaText, "runner:" + h.runnerId); }
                catch (Exception e) { thrown = Clean(e.Message); }

                LuaTable t = env.Global.Get<LuaTable>("__dp_rh");
                int count = env.Global.Get<int>("__dp_rh_count");
                if (t == null)
                {
                    h.error = thrown != null
                        ? "the script did not run up to its header: " + thrown
                        : "no DreamPark.runner { name = …, blurb = …, cover = …, default = … } header at the top of the file";
                    return h;
                }
                h.hasHeader = true;
                if (thrown != null) h.warnings.Add("the file stopped after its header: " + thrown + " (fine on the headset only if that line runs inside a callback)");
                if (count > 1) h.error = "DreamPark.runner { … } is called " + count + " times; a runner has exactly one header";

                h.name = ReadString(t, "name");
                h.blurb = ReadString(t, "blurb");
                h.cover = ReadString(t, "cover").Replace('\\', '/');
                object def = t.Get<object>("default");
                if (def is bool b) h.isDefault = b;
                else if (def != null) h.warnings.Add("default should be true or false");

                foreach (object key in t.GetKeys())
                {
                    string k = key as string;
                    if (k != "name" && k != "blurb" && k != "cover" && k != "default")
                        h.warnings.Add("unknown header field '" + key + "' (ignored)");
                }

                if (string.IsNullOrEmpty(h.error))
                {
                    if (string.IsNullOrWhiteSpace(h.name)) h.error = "the header needs a name (what the phone shows, e.g. \"PvP only\")";
                    else if (h.name.Length > MaxName) h.error = "name is longer than " + MaxName + " characters";
                    else if (h.blurb.Length > MaxBlurb) h.error = "blurb is longer than " + MaxBlurb + " characters";
                }
                t.Dispose();
            }
            catch (Exception e)
            {
                h.error = "could not read the header: " + Clean(e.Message);
            }
            finally
            {
                try { env?.Dispose(); } catch { /* a leaked delegate must not fail the read */ }
            }
            return h;
        }

        /// Exactly one default: the marked one, else the first by runnerId. Returns the chosen
        /// index (−1 for an empty list) and writes isDefault on every header. `twoMarked` = more than one marked.
        public static int ResolveDefault(IList<RunnerHeaderInfo> headers, out bool twoMarked)
        {
            twoMarked = false;
            if (headers == null || headers.Count == 0) return -1;
            int chosen = -1;
            for (int i = 0; i < headers.Count; i++)
            {
                if (!headers[i].isDefault) continue;
                if (chosen < 0) chosen = i; else twoMarked = true;
            }
            if (chosen < 0)
            {
                chosen = 0;
                for (int i = 1; i < headers.Count; i++)
                    if (string.CompareOrdinal(headers[i].runnerId, headers[chosen].runnerId) < 0) chosen = i;
            }
            for (int i = 0; i < headers.Count; i++) headers[i].isDefault = i == chosen;
            return chosen;
        }

        static string ReadString(LuaTable t, string key)
        {
            object v = t.Get<object>(key);
            return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture).Trim();
        }

        static string Clean(string m)
        {
            if (string.IsNullOrEmpty(m)) return "error";
            int nl = m.IndexOf('\n');
            if (nl > 0) m = m.Substring(0, nl);
            return m.Replace("[string \"", "[").Trim();
        }
    }
}
