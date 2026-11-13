// ─────────────────────────────────────────────────────────────────────
//  RunnerRecord.cs — a WAY TO PLAY as a headset gets it (ways-to-play
//  contract §3): the director Lua plus its header, and the platform's
//  built-in default runner (levels in order, one per tile). Pure.
//
//  Also: IRunnerEdgeSocket, the seam to the SDK's EdgeSocket component
//  (sdk-tools agent) — the platform dresses tile edges through it.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DreamPark
{
    [Serializable]
    public sealed class RunnerRecord
    {
        /// The script's file name lower-cased (pvp.lua → "pvp"); "default" for the built-in one.
        public string runnerId;
        public string name;
        public string blurb;
        /// The header's cover path (the release's coverUrl is the server's copy).
        public string cover;
        public bool isDefault;
        /// The director script's text.
        [UnityEngine.TextArea(3, 20)] public string lua;

        public bool IsBuiltIn => string.Equals(runnerId, RunnerDefaults.BuiltInId, StringComparison.Ordinal) && string.IsNullOrEmpty(lua);

        public static RunnerRecord FromLua(string runnerId, string lua)
        {
            var h = RunnerHeader.Parse(lua);
            return new RunnerRecord { runnerId = runnerId, lua = lua, name = h.name ?? runnerId, blurb = h.blurb ?? "", cover = h.cover ?? "", isDefault = h.isDefault };
        }

        public override string ToString() => $"{runnerId}('{name}'{(isDefault ? ", default" : "")})";
    }

    /// The `DreamPark.runner { name = "...", blurb = "...", cover = "...", default = true }` header, read WITHOUT running
    /// the script (the uploader and the phone list need it before anything runs).
    public sealed class RunnerHeader
    {
        public string name, blurb, cover;
        public bool isDefault;
        public bool found;

        static readonly Regex Block = new Regex(@"DreamPark\s*\.\s*runner\s*\(?\s*\{(?<b>.*?)\}", RegexOptions.Singleline);
        static readonly Regex Str = new Regex(@"(?<k>name|blurb|cover)\s*=\s*(?:""(?<v>(?:[^""\\]|\\.)*)""|'(?<v>(?:[^'\\]|\\.)*)'|\[\[(?<v>.*?)\]\])", RegexOptions.Singleline);
        static readonly Regex Def = new Regex(@"(?<![\w.])default\s*=\s*(?<v>true|false)");

        public static RunnerHeader Parse(string lua)
        {
            var h = new RunnerHeader();
            if (string.IsNullOrEmpty(lua)) return h;
            string src = Regex.Replace(lua, @"--\[(=*)\[.*?\]\1\]", "", RegexOptions.Singleline);
            src = Regex.Replace(src, @"--[^\n]*", "");
            var m = Block.Match(src);
            if (!m.Success) return h;
            h.found = true;
            string b = m.Groups["b"].Value;
            foreach (Match s in Str.Matches(b))
            {
                string v = Regex.Unescape(s.Groups["v"].Value);
                switch (s.Groups["k"].Value) { case "name": h.name = v; break; case "blurb": h.blurb = v; break; case "cover": h.cover = v; break; }
            }
            var d = Def.Match(b);
            h.isDefault = d.Success && d.Groups["v"].Value == "true";
            return h;
        }
    }

    public static class RunnerDefaults
    {
        public const string BuiltInId = "default";

        /// A title with no runner: its levels in order, one per tile; a finished tile swaps to the next level not on the
        /// board once it is empty (cycling). Written against the same dp.flow API every author uses.
        public const string BuiltInLua = @"
DreamPark.runner { name = 'Play', blurb = 'Every level, one after another.', default = true }

local function nextLevel()
    local lv = dp.flow.levels()
    if #lv == 0 then return nil end
    local onBoard = {}
    for _, t in ipairs(dp.flow.board().list) do if t.level then onBoard[t.level] = true end end
    local i = dp.flow.state.cursor or 0
    for k = 1, #lv do
        local c = ((i + k - 1) % #lv) + 1
        if not onBoard[lv[c].id] then dp.flow.state.cursor = c return lv[c].id end
    end
    return nil
end

function onSessionStart(board)
    local lv = dp.flow.levels()
    local n = math.min(board.tiles, #lv)
    for i = 1, n do dp.flow.load(lv[i].id, { tile = i }) end
    dp.flow.state.cursor = n
end

dp.flow.onLevelComplete(function(level, info)
    local nxt = nextLevel()
    if nxt and info.tile then dp.flow.swapWhenClear(info.tile, nxt) end
end)
";

        public static RunnerRecord BuiltIn(string titleName = null) => new RunnerRecord
        {
            runnerId = BuiltInId, name = string.IsNullOrEmpty(titleName) ? "Play" : titleName, blurb = "", isDefault = true, lua = null,
        };

        public static string LuaFor(RunnerRecord r) => r == null || string.IsNullOrWhiteSpace(r.lua) ? BuiltInLua : r.lua;

        /// The runner to play: the id asked for, else the record marked default, else the first, else the built-in.
        public static RunnerRecord Pick(IList<RunnerRecord> runners, string runnerId, string titleName = null)
        {
            if (runners != null && runners.Count > 0)
            {
                if (!string.IsNullOrEmpty(runnerId))
                    foreach (var r in runners) if (r != null && string.Equals(r.runnerId, runnerId, StringComparison.OrdinalIgnoreCase)) return r;
                foreach (var r in runners) if (r != null && r.isDefault) return r;
                foreach (var r in runners) if (r != null) return r;
            }
            return BuiltIn(titleName);
        }
    }

    /// The SDK's EdgeSocket implements this: one dressed opening on a tile edge of a level (closed wall look when the
    /// level stands alone, an open doorway / gate when a neighbour tile is joined). Sides are level-local at its Base
    /// layout: "left" −X, "right" +X, "front" −Z (toward the portal), "back" +Z.
    public interface IRunnerEdgeSocket
    {
        string EdgeSide { get; }
        /// look: "closed" | "open" | "doorway" | "gate" (unknown looks = the socket's open look); locked = a gate held shut.
        void ApplyEdge(string look, bool locked);
    }
}
