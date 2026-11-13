#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamPark.Editor.GameGraph
{
    /// <summary>
    /// The Game Graph's Lua index (Docs/GAME-GRAPH.md § lua): functions, globals, platform hooks,
    /// dp.* calls with their literal arguments, signals (event names leaving a script), badge award
    /// sites and fall/respawn hints. Regex-level but comment- and string-aware, deterministic.
    /// Mirrors dreampark-core Tools/Dreamie/game-graphs/tool/luaIndex.js: keep the two in step.
    /// </summary>
    public static class GameGraphLuaIndex
    {
        public static readonly string[] LifecycleHooks =
        {
            "awake", "start", "update", "fixedupdate", "lateupdate", "ondestroy", "onenable", "ondisable",
            "onnet", "onwalls", "on_rect_changed", "onlayout", "ontriggerenter", "ontriggerexit", "ontriggerstay",
            "oncollisionenter", "oncollisionexit", "oncollisionstay", "onapplicationpause", "onapplicationfocus",
            "onzoneenter", "onzoneexit", "onready", "onpause", "onresume",
        };
        public static readonly string[] PlatformHooks =
        {
            "start_challenge", "stop_challenge", "end_challenge", "dreamie_describe", "dreamie_select_mission",
            "select_mission", "mission_count", "mission_name", "standing_mission", "pick_mission",
        };
        static readonly HashSet<string> Hooks = new HashSet<string>(LifecycleHooks.Concat(PlatformHooks), StringComparer.Ordinal);

        static readonly Regex EmitterRx = new Regex(@"^(signal|emit|broadcast|send|fire|notify|net_send|send_event|trigger_event|raise|post|dispatch|award[A-Za-z_]*|report[A-Za-z_]*|announce|cue|play_cue)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ListenRx = new Regex(@"^(on|on_[a-z0-9_]+|once|subscribe|listen|add_?listener|register|bind|handle|connect|when|watch)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SayRx = new Regex(@"^(say|speak|bubble|show_bubble|speech|show_text|set_text|toast|announce|caption|subtitle|dialog|dialogue|talk|popup|banner|show_hint|hint|tell|narrate|show_message|title_card|callout|shout)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SayFieldRx = new Regex(@"(^|[\s{,])(say|text|line|caption|hint|title|subtitle|speech|bubble|label|message|msg)\s*=\s*$", RegexOptions.Compiled);
        static readonly Regex ProseRx = new Regex(@"[A-Za-z].*\s.*[A-Za-z]", RegexOptions.Compiled);
        static readonly Regex ProseFieldRx = new Regex(@"[A-Za-z].*\s.*[A-Za-z].*\s", RegexOptions.Compiled);
        static readonly Regex CmpRx = new Regex(@"(==|~=)\s*([""'])|([""'])[^""'\n]*\3\s*(==|~=)", RegexOptions.Compiled);
        static readonly HashSet<string> NotEvents = new HashSet<string>(LifecycleHooks, StringComparer.Ordinal);
        static readonly Regex FallRx = new Regex(@"\b[A-Za-z_]*(respawn|kill_?plane|kill_?y|killz|fell|falling|fall_?off|fall_?death|out_?of_?bounds|oob|void_?y|drown|teleport_?back|reset_?player|last_?safe|safe_?spot|checkpoint)[A-Za-z_]*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex FallYRx = new Regex(@"\.y\s*<=?\s*-?\d|\.y\s*<=?\s*[A-Za-z_]*(floor|ground|kill|min|death|lava|water)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SrcFallRx = new Regex(@"[""'](Lava|Water|KillZone|Kill|Death|DeathZone|Void|Respawn|Checkpoint|Hazard)[""']", RegexOptions.Compiled);

        static readonly Regex FnDeclRx = new Regex(@"(^|[^\w.:])(local\s+)?function\s+([A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*)\s*\(([^)]*)\)", RegexOptions.Compiled);
        static readonly Regex FnAssignRx = new Regex(@"(^|\n)([ \t]*)(local\s+)?([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*|\[\s*""[^""]*""\s*\])\s*=\s*function\s*\(([^)]*)\)", RegexOptions.Compiled);
        static readonly Regex TopAssignRx = new Regex(@"(^|\n)([A-Za-z_]\w*)\s*(?:,\s*[A-Za-z_]\w*\s*)*=(?!=)", RegexOptions.Compiled);
        static readonly Regex GAssignRx = new Regex(@"\b_G\.([A-Za-z_]\w*)\s*=(?!=)", RegexOptions.Compiled);
        static readonly Regex GReadRx = new Regex(@"\b_G\.([A-Za-z_]\w*)", RegexOptions.Compiled);
        static readonly Regex DpRx = new Regex(@"\bdp\.([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(", RegexOptions.Compiled);
        static readonly Regex CallRx = new Regex(@"([A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*)\s*\(", RegexOptions.Compiled);
        static readonly Regex IdentRx = new Regex(@"\b([a-z][a-z0-9_]{2,})\b", RegexOptions.Compiled);
        static readonly Regex SignalNameRx = new Regex(@"^[A-Za-z_][\w.\-:]{0,63}$", RegexOptions.Compiled);
        /// Announcements (PLAYLIST-PLAN § 4.2): dp.announce("k", {…}), an alias / chain of dp (d.announce, c.dp.announce) and the
        /// guarded pcall forms (pcall(dp.announce, "k", t)). Only the eight kinds count. Mirrors luaIndex.js ANNOUNCE_RE.
        public static readonly string[] AnnounceKinds = { "collect", "defeat", "break", "target", "reach", "find", "protect", "catch" };
        static readonly Regex AnnounceRx = new Regex(@"(\bpcall\s*\(\s*)?\b((?:[A-Za-z_]\w*\s*\.\s*)*[A-Za-z_]\w*)\s*\.\s*announce\s*([(,])", RegexOptions.Compiled);
        static readonly Regex ItemFieldRx = new Regex(@"\bitem\s*=\s*([""'])", RegexOptions.Compiled);
        static readonly Regex HoldsFieldRx = new Regex(@"\bholds\s*=\s*\{([^{}]*)\}", RegexOptions.Compiled);
        static readonly Regex KeyRx = new Regex(@"([A-Za-z_]\w*)\s*=", RegexOptions.Compiled);
        static readonly Regex FromFieldRx = new Regex(@"\bfrom\s*=", RegexOptions.Compiled);
        static readonly Regex ItemNameRx = new Regex(@"^[A-Za-z_][\w\-]{0,40}$", RegexOptions.Compiled);
        /// Kits: libraries published on / read from _G (rawset(_G, "x", …) / rawget(_G, "x") / dp.on_global("x", …)).
        static readonly Regex KitPubRx = new Regex(@"\brawset\s*\(\s*_G\s*,|\bset_global\s*\(", RegexOptions.Compiled);
        static readonly Regex KitUseRx = new Regex(@"\brawget\s*\(\s*_G\s*,|\bon_global\s*\(|\bget_global\s*\(", RegexOptions.Compiled);
        static readonly Regex KitNameRx = new Regex(@"^[A-Za-z_]\w{0,63}$", RegexOptions.Compiled);
        static readonly string[] NotGlobals = { "local", "return", "if", "for", "while", "end", "else", "function" };
        const int CapLines = 12;

        struct Str { public int at, end; public string value; public bool isLong; }

        sealed class Hits
        {
            public int n;
            public readonly List<int> lines = new List<int>();
            public SortedSet<string> extra;
        }

        /// <summary>Indexes one Lua source → the graph's per-script object (without path/bytes).</summary>
        public static JObj Index(string src, ICollection<string> badgeIds = null, ICollection<string> signalVocab = null)
        {
            src = src ?? "";
            Scan(src, out string code, out List<Str> strings);
            List<int> starts = LineStarts(src);
            int Ln(int off) => LineOf(starts, off);

            // ── functions ──
            var functions = new List<(string name, int line, string[] pars, string scope)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void AddFn(string name, string pars, int off, bool isLocal, bool assign)
            {
                int line = Ln(off);
                if (!seen.Add(name + "@" + line)) return;
                int col = off - starts[line - 1];
                string scope = isLocal ? "local" : (name.IndexOf('.') >= 0 || name.IndexOf(':') >= 0) ? "member"
                    : (assign && col > 0) ? "field" : "global";
                string[] ps = pars.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                functions.Add((name, line, ps, scope));
            }
            foreach (Match m in FnDeclRx.Matches(code))
                AddFn(m.Groups[3].Value, m.Groups[4].Value, m.Index + m.Groups[1].Length, m.Groups[2].Success, false);
            foreach (Match m in FnAssignRx.Matches(code))
            {
                string name = m.Groups[4].Value;
                int at = m.Index + m.Groups[1].Length + m.Groups[2].Length;
                if (name.StartsWith("[", StringComparison.Ordinal))
                {
                    Str? s = StringAt(strings, at);
                    if (s.HasValue) name = s.Value.value;
                }
                AddFn(name, m.Groups[5].Value, at, m.Groups[3].Success, true);
            }
            functions.Sort((a, b) => a.line != b.line ? a.line.CompareTo(b.line) : string.CompareOrdinal(a.name, b.name));

            // ── globals ──
            var globals = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in TopAssignRx.Matches(code)) if (Array.IndexOf(NotGlobals, m.Groups[2].Value) < 0) globals.Add(m.Groups[2].Value);
            foreach (Match m in GAssignRx.Matches(code)) globals.Add(m.Groups[1].Value);
            for (int k = code.IndexOf("_G[", StringComparison.Ordinal); k >= 0; k = code.IndexOf("_G[", k + 3, StringComparison.Ordinal))
            {
                int q = k + 3; while (q < code.Length && char.IsWhiteSpace(code[q])) q++;
                Str? s = StringAt(strings, q);
                if (s.HasValue && s.Value.at == q) globals.Add(s.Value.value);
            }
            foreach (var f in functions) if (f.scope == "global") globals.Add(f.name);
            var reads = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in GReadRx.Matches(code)) if (!globals.Contains(m.Groups[1].Value)) reads.Add(m.Groups[1].Value);

            // ── dp.* calls ──
            var dp = new SortedDictionary<string, Hits>(StringComparer.Ordinal);
            foreach (Match m in DpRx.Matches(code))
                Push(dp, "dp." + m.Groups[1].Value, Ln(m.Index), FirstArgString(code, strings, m.Index + m.Length));

            // ── signals ──
            var signals = new SortedDictionary<string, Hits>(StringComparer.Ordinal);
            var badges = new SortedDictionary<string, Hits>(StringComparer.Ordinal);
            foreach (Match m in CallRx.Matches(code))
            {
                string callee = m.Groups[1].Value;
                if (callee == "function") continue;
                string before = code.Substring(Math.Max(0, m.Index - 9), m.Index - Math.Max(0, m.Index - 9));
                if (Regex.IsMatch(before, @"function\s*$")) continue;
                string last = callee.Split('.', ':').Last();
                string arg = FirstArgString(code, strings, m.Index + m.Length);
                if (EmitterRx.IsMatch(last) && arg != null && SignalNameRx.IsMatch(arg)) Push(signals, arg, Ln(m.Index), callee);
                if (callee.EndsWith("awardBadge", StringComparison.OrdinalIgnoreCase)) Push(badges, arg ?? "(dynamic)", Ln(m.Index), "award");
            }
            if (signalVocab != null && signalVocab.Count > 0)
                foreach (Str s in strings) if (signalVocab.Contains(s.value)) Push(signals, s.value, Ln(s.at), "literal");
            if (badgeIds != null && badgeIds.Count > 0)
            {
                foreach (Str s in strings) if (badgeIds.Contains(s.value)) Push(badges, s.value, Ln(s.at), "mention");
                foreach (Match m in IdentRx.Matches(code)) if (badgeIds.Contains(m.Groups[1].Value)) Push(badges, m.Groups[1].Value, Ln(m.Index), "mention");
            }

            foreach (string k in signals.Keys.ToList()) if (NotEvents.Contains(k.ToLowerInvariant())) signals.Remove(k);

            // ── listeners (registrations), compared names, dialogue ──
            var listens = new SortedDictionary<string, Hits>(StringComparer.Ordinal);
            var compares = new SortedDictionary<string, Hits>(StringComparer.Ordinal);
            var dialogue = new List<(int line, string text)>();
            foreach (Match m in CallRx.Matches(code))
            {
                string callee = m.Groups[1].Value;
                string last = callee.Split('.', ':').Last();
                string before = code.Substring(Math.Max(0, m.Index - 9), m.Index - Math.Max(0, m.Index - 9));
                if (Regex.IsMatch(before, @"function\s*$")) continue;
                string lit = FirstArgString(code, strings, m.Index + m.Length);
                if (lit == null) continue;
                if (ListenRx.IsMatch(last) && SignalNameRx.IsMatch(lit) && !NotEvents.Contains(lit.ToLowerInvariant())) Push(listens, lit, Ln(m.Index), callee);
                if (SayRx.IsMatch(last) && ProseRx.IsMatch(lit) && dialogue.Count < 40) dialogue.Add((Ln(m.Index), lit.Length > 160 ? lit.Substring(0, 160) : lit));
            }
            foreach (Match m in CmpRx.Matches(code))
            {
                int at = m.Groups[2].Success ? m.Index + m.Length - 1 : m.Index;
                Str? st = StringAt(strings, at);
                if (st.HasValue && st.Value.at == at && SignalNameRx.IsMatch(st.Value.value) && !NotEvents.Contains(st.Value.value.ToLowerInvariant())) Push(compares, st.Value.value, Ln(at), null);
            }
            foreach (Str st in strings)
            {
                if (dialogue.Count >= 40) break;
                if (!ProseFieldRx.IsMatch(st.value) || st.isLong) continue;
                int from = Math.Max(0, st.at - 24);
                if (SayFieldRx.IsMatch(code.Substring(from, st.at - from))) dialogue.Add((Ln(st.at), st.value.Length > 160 ? st.value.Substring(0, 160) : st.value));
            }
            dialogue.Sort((x, y) => x.line != y.line ? x.line.CompareTo(y.line) : string.CompareOrdinal(x.text, y.text));

            // ── announcements ──
            var announces = new SortedDictionary<string, (Hits hits, SortedSet<string> items, SortedSet<string> holds, bool from)>(StringComparer.Ordinal);
            foreach (Match m in AnnounceRx.Matches(code))
            {
                bool isPcall = m.Groups[1].Success && m.Groups[1].Length > 0;
                string open = m.Groups[3].Value;
                if (open == "," && !isPcall) continue;          // a bare reference passed along, not a call
                if (open == "(" && isPcall) continue;           // pcall(x.announce(...)): the inner call is matched on its own
                string before = code.Substring(Math.Max(0, m.Index - 9), m.Index - Math.Max(0, m.Index - 9));
                if (Regex.IsMatch(before, @"function\s*$")) continue;
                int after = m.Index + m.Length;
                string kind = FirstArgString(code, strings, after);
                if (kind == null || Array.IndexOf(AnnounceKinds, kind) < 0) continue;
                int ka = after; while (ka < code.Length && char.IsWhiteSpace(code[ka])) ka++;
                Str? ks = StringAt(strings, ka);
                int kEnd = ks.HasValue ? ks.Value.end : after;
                var info = AnnounceOpts(code, strings, kEnd, m.Index);
                string via = (isPcall ? "pcall " : "") + Regex.Replace(m.Groups[2].Value, @"\s+", "") + ".announce";
                if (!announces.TryGetValue(kind, out var e)) e = (new Hits(), new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal), false);
                e.hits.n++;
                if (e.hits.lines.Count < CapLines) e.hits.lines.Add(Ln(m.Index));
                (e.hits.extra ??= new SortedSet<string>(StringComparer.Ordinal)).Add(via);
                e.items.UnionWith(info.items); e.holds.UnionWith(info.holds);
                announces[kind] = (e.hits, e.items, e.holds, e.from || info.from);
            }

            // ── kits (libraries published on / read from _G) ──
            var kitPub = new SortedSet<string>(StringComparer.Ordinal);
            var kitUse = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in KitPubRx.Matches(code)) { string v = FirstArgString(code, strings, m.Index + m.Length); if (v != null && KitNameRx.IsMatch(v)) kitPub.Add(v); }
            foreach (Match m in KitUseRx.Matches(code)) { string v = FirstArgString(code, strings, m.Index + m.Length); if (v != null && KitNameRx.IsMatch(v)) kitUse.Add(v); }
            foreach (Match m in GReadRx.Matches(code)) kitUse.Add(m.Groups[1].Value);
            foreach (Match m in GAssignRx.Matches(code)) kitPub.Add(m.Groups[1].Value);
            kitUse.ExceptWith(kitPub);

            // ── fall / respawn hints ──
            var fall = new List<object>();
            string[] codeLines = code.Split('\n');
            string[] srcLines = src.Split('\n');
            for (int i = 0; i < codeLines.Length && fall.Count < 12; i++)
            {
                string c = codeLines[i];
                string s = i < srcLines.Length ? srcLines[i] : "";
                if (FallRx.IsMatch(c) || FallYRx.IsMatch(c) || (SrcFallRx.IsMatch(s) && (c.IndexOf('"') >= 0 || c.IndexOf('\'') >= 0)))
                {
                    string t = s.Trim();
                    fall.Add(new List<object> { i + 1, t.Length > 120 ? t.Substring(0, 120) : t });
                }
            }

            var hooks = new SortedSet<string>(functions.Where(f => (f.scope == "global" || f.scope == "local") && Hooks.Contains(f.name)).Select(f => f.name), StringComparer.Ordinal);

            var o = new JObj();
            o.Add("lines", starts.Count);
            o.Add("functions", functions.Select(f => (object)new JObj().Add("name", f.name).Add("line", f.line).Add("params", f.pars).Add("scope", f.scope)).ToList());
            o.Add("globals", globals.ToList());
            o.Add("globalsRead", reads.ToList());
            o.Add("hooks", hooks.ToList());
            o.Add("dp", ToObj(dp, "args"));
            o.Add("signals", ToObj(signals, "via"));
            o.Add("badges", ToObj(badges, "how"));
            o.Add("listens", ToObj(listens, "via"));
            o.Add("compares", ToObj(compares, null));
            o.Add("dialogue", dialogue.Select(d => (object)new List<object> { d.line, d.text }).ToList());
            o.Add("fall", fall);
            o.Add("kits", new JObj().Add("publishes", kitPub.ToList()).Add("uses", kitUse.ToList()));
            var an = new JObj();
            foreach (var kv in announces)
            {
                var a = new JObj().Add("n", kv.Value.hits.n).Add("lines", kv.Value.hits.lines.ToList()).Add("via", (kv.Value.hits.extra ?? new SortedSet<string>()).ToList());
                if (kv.Value.items.Count > 0) a.Add("items", kv.Value.items.ToList());
                if (kv.Value.holds.Count > 0) a.Add("holds", kv.Value.holds.ToList());
                if (kv.Value.from) a.Add("from", true);
                an.Add(kv.Key, a);
            }
            o.Add("announces", an);
            return o;
        }

        /// The options of one announce call: the table literal right after the kind, or — when the options are a variable
        /// (local t = { item = … }; t.holds = { collect = 1 }; pcall(dp.announce, "k", t)) — the 12 lines before the call.
        static (List<string> items, List<string> holds, bool from) AnnounceOpts(string code, List<Str> strings, int kEnd, int callAt)
        {
            var items = new List<string>(); var holds = new List<string>();
            int k = kEnd;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
            int a = -1, b = -1;
            if (k < code.Length && code[k] == ',')
            {
                k++;
                while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
                if (k < code.Length && code[k] == '{')
                {
                    int depth = 0, e = k;
                    for (; e < code.Length; e++) { if (code[e] == '{') depth++; else if (code[e] == '}' && --depth == 0) break; }
                    a = k; b = Math.Min(code.Length, e + 1);
                }
                else if (k < code.Length && (char.IsLetter(code[k]) || code[k] == '_'))
                {
                    int from = callAt, lines = 0;
                    while (from > 0 && lines < 12) { from--; if (code[from] == '\n') lines++; }
                    a = from; b = callAt;
                }
            }
            if (a < 0) return (items, holds, false);
            string body = code.Substring(a, b - a);
            foreach (Match m in ItemFieldRx.Matches(body))
            {
                int at = a + m.Index + m.Length - 1;
                Str? s = StringAt(strings, at);
                if (s.HasValue && s.Value.at == at && ItemNameRx.IsMatch(s.Value.value)) items.Add(s.Value.value);
            }
            foreach (Match m in HoldsFieldRx.Matches(body))
                foreach (Match km in KeyRx.Matches(m.Groups[1].Value))
                    if (Array.IndexOf(AnnounceKinds, km.Groups[1].Value) >= 0) holds.Add(km.Groups[1].Value);
            return (items, holds, FromFieldRx.IsMatch(body));
        }

        static void Push(SortedDictionary<string, Hits> map, string key, int line, string extra)
        {
            if (!map.TryGetValue(key, out Hits h)) map[key] = h = new Hits();
            h.n++;
            if (h.lines.Count < CapLines) h.lines.Add(line);
            if (!string.IsNullOrEmpty(extra)) (h.extra ??= new SortedSet<string>(StringComparer.Ordinal)).Add(extra);
        }

        static JObj ToObj(SortedDictionary<string, Hits> map, string extraName)
        {
            var o = new JObj();
            foreach (var kv in map)
            {
                var e = new JObj().Add("n", kv.Value.n).Add("lines", kv.Value.lines.ToList());
                if (extraName != null && kv.Value.extra != null && kv.Value.extra.Count > 0) e.Add(extraName, kv.Value.extra.Take(12).ToList());
                o.Add(kv.Key, e);
            }
            return o;
        }

        /// <summary>Comments blanked (newlines kept); string literal contents blanked but quotes kept.</summary>
        static void Scan(string src, out string code, out List<Str> strings)
        {
            var o = src.ToCharArray();
            strings = new List<Str>();
            int n = src.Length, i = 0;
            void Blank(int a, int b) { for (int k = a; k < b && k < n; k++) if (o[k] != '\n') o[k] = ' '; }
            int LongOpen(int at)
            {
                if (at >= n || src[at] != '[') return -1;
                int k = at + 1, eq = 0;
                while (k < n && src[k] == '=') { eq++; k++; }
                return k < n && src[k] == '[' ? eq : -1;
            }
            int LongClose(int from, int eq)
            {
                string close = "]" + new string('=', eq) + "]";
                int at = from <= n ? src.IndexOf(close, Math.Min(from, n), StringComparison.Ordinal) : -1;
                return at < 0 ? n : at + close.Length;
            }
            while (i < n)
            {
                char c = src[i];
                if (c == '-' && i + 1 < n && src[i + 1] == '-')
                {
                    int eq = LongOpen(i + 2);
                    int end;
                    if (eq >= 0) end = LongClose(i + 2 + eq + 2, eq);
                    else { end = src.IndexOf('\n', i); if (end < 0) end = n; }
                    Blank(i, end); i = end; continue;
                }
                if (c == '"' || c == '\'')
                {
                    int k = i + 1;
                    var val = new StringBuilder();
                    while (k < n && src[k] != c && src[k] != '\n')
                    {
                        if (src[k] == '\\' && k + 1 < n) { val.Append(src[k + 1]); k += 2; continue; }
                        val.Append(src[k]); k++;
                    }
                    strings.Add(new Str { at = i, end = k + 1, value = val.ToString() });
                    Blank(i + 1, k);
                    i = k + 1; continue;
                }
                if (c == '[')
                {
                    int eq = LongOpen(i);
                    if (eq >= 0)
                    {
                        int bodyStart = i + eq + 2;
                        int end = LongClose(bodyStart, eq);
                        int bodyEnd = Math.Max(bodyStart, end - eq - 2);
                        strings.Add(new Str { at = i, end = end, isLong = true, value = bodyEnd > bodyStart && bodyEnd <= n ? src.Substring(bodyStart, bodyEnd - bodyStart) : "" });
                        Blank(i + 1, end - 1);
                        i = end; continue;
                    }
                }
                i++;
            }
            code = new string(o);
        }

        static List<int> LineStarts(string src)
        {
            var s = new List<int> { 0 };
            for (int i = 0; i < src.Length; i++) if (src[i] == '\n') s.Add(i + 1);
            return s;
        }

        static int LineOf(List<int> starts, int offset)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (starts[mid] <= offset) lo = mid; else hi = mid - 1; }
            return lo + 1;
        }

        static Str? StringAt(List<Str> strings, int offset)
        {
            int lo = 0, hi = strings.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (strings[mid].at < offset) lo = mid + 1; else hi = mid; }
            return lo < strings.Count ? strings[lo] : (Str?)null;
        }

        static string FirstArgString(string code, List<Str> strings, int from)
        {
            int k = from;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
            if (k >= code.Length) return null;
            bool longStr = code[k] == '[' && Regex.IsMatch(code.Substring(k, Math.Min(8, code.Length - k)), @"^\[=*\[");
            if (code[k] == '"' || code[k] == '\'' || longStr)
            {
                Str? s = StringAt(strings, k);
                if (s.HasValue && s.Value.at == k) return s.Value.value;
            }
            return null;
        }
    }
}
#endif
