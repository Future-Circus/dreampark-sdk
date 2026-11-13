#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamPark.Editor.GameGraph
{
    /// <summary>
    /// An insertion-ordered JSON object for the Game Graph (Docs/GAME-GRAPH.md). Deterministic:
    /// the writer emits keys in the order they were added and numbers in invariant culture, so
    /// the same project exports byte-identical JSON and release diffs are meaningful.
    /// </summary>
    public sealed class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();
        readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count => items.Count;

        /// Adds or replaces. A null value is skipped (absent ≠ null in this schema).
        public JObj Add(string key, object value)
        {
            if (value == null) return this;
            if (index.TryGetValue(key, out int i)) { items[i] = new KeyValuePair<string, object>(key, value); return this; }
            index[key] = items.Count;
            items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        /// Adds or replaces, keeping a null value (the JSON reader: `"place": null` is present-but-null).
        public JObj AddRaw(string key, object value)
        {
            if (index.TryGetValue(key, out int i)) { items[i] = new KeyValuePair<string, object>(key, value); return this; }
            index[key] = items.Count;
            items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        public object this[string key] => index.TryGetValue(key, out int i) ? items[i].Value : null;
        public bool Has(string key) => index.ContainsKey(key);
        public bool Remove(string key)
        {
            if (!index.TryGetValue(key, out int i)) return false;
            items.RemoveAt(i);
            index.Clear();
            for (int k = 0; k < items.Count; k++) index[items[k].Key] = k;
            return true;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    }

    public static class GameGraphJson
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder(1 << 16);
            WriteValue(sb, value);
            return sb.ToString();
        }

        /// A number rounded to `decimals` (no "-0", no exponent for normal magnitudes).
        public static double Round(double v, int decimals = 3)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            double r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
            return r == 0 ? 0 : r;
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case float f: WriteNumber(sb, f); return;
                case double d: WriteNumber(sb, d); return;
                case JObj o:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteValue(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                }
                case IDictionary<string, string> ds:
                {
                    var keys = new List<string>(ds.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    sb.Append('{');
                    for (int k = 0; k < keys.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        WriteString(sb, keys[k]); sb.Append(':'); WriteString(sb, ds[keys[k]]);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable e:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object x in e)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, x);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('0'); return; }
            if (d == 0) { sb.Append('0'); return; }
            sb.Append(d.ToString("0.############", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>
    /// A SETS playlist's export (PLAYLIST-PLAN § 3 / § 3.4): `pool` [{id, attraction, challenge, options, role, place, lock,
    /// displayName}] (present fields only; options when a non-empty object; place / lock verbatim), `requires` {badges} when
    /// the record gates on badges, `finishBadge` = the badge its Lua awards at the end. Mirrors dreampark-core
    /// Tools/Dreamie/game-graphs/tool/build.js playlistSets / finishBadge: the output must be byte-identical.
    /// </summary>
    public static class GameGraphPlaylistSets
    {
        /// `record` = the authored playlist object (GameGraphJson.Read of PackagePlaylist.sourceJson); resolve(attraction, contentId).
        public static JObj Fields(JObj record, string lua, Func<string, string, string> resolve)
        {
            var o = new JObj();
            string Str(object v) => v is string s && s.Length > 0 ? s : null;
            if (record != null && record["pool"] is List<object> pool && pool.Count > 0)
            {
                var list = new List<object>();
                foreach (object x in pool)
                {
                    if (!(x is JObj e)) continue;
                    var po = new JObj();
                    if (Str(e["id"]) != null) po.Add("id", e["id"]);
                    if (Str(e["attraction"]) != null) po.Add("attraction", resolve((string)e["attraction"], Str(e["contentId"])));
                    if (Str(e["challenge"]) != null) po.Add("challenge", e["challenge"]);
                    if (e["options"] is JObj op && op.Count > 0) po.Add("options", op);
                    if (Str(e["role"]) != null) po.Add("role", e["role"]);
                    if (e.Has("place") && e["place"] != null && !(e["place"] is string ps && ps.Length == 0)) po.Add("place", e["place"]);
                    if (e.Has("lock") && e["lock"] != null && !(e["lock"] is bool lb && !lb)) po.Add("lock", e["lock"]);
                    if (Str(e["displayName"]) != null) po.Add("displayName", e["displayName"]);
                    list.Add(po);
                }
                o.Add("pool", list);
            }
            if (record != null && record["requires"] is JObj rq && rq["badges"] is List<object> bl)
            {
                var badges = bl.OfType<string>().Where(b => b.Length > 0).Cast<object>().ToList();
                if (badges.Count > 0) o.Add("requires", new JObj().Add("badges", badges));
            }
            string fb = FinishBadge(lua);
            if (fb != null) o.Add("finishBadge", fb);
            return o;
        }

        static readonly Regex CommentRx = new Regex(@"--[^\n]*", RegexOptions.Compiled);
        static readonly Regex BatchesRx = new Regex(@"\brun\.batches\s*[({][\s\S]*?\bbadge\s*=\s*[""']([^""'\n]+)[""']", RegexOptions.Compiled);
        static readonly Regex BadgeRx = new Regex(@"\brun\.badge\s*\(\s*(?:[A-Za-z_][\w.]*\s*,\s*)?[""']([^""'\n]+)[""']", RegexOptions.Compiled);

        /// The badge a playlist's Lua awards at its end: run.batches{… badge = 'x' …}, else the first run.badge('x') / run.badge(p, 'x').
        public static string FinishBadge(string lua)
        {
            if (string.IsNullOrEmpty(lua)) return null;
            string code = CommentRx.Replace(lua, "");
            Match m = BatchesRx.Match(code);
            if (m.Success) return m.Groups[1].Value;
            m = BadgeRx.Match(code);
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    /// <summary>A small order-preserving JSON reader (objects → JObj, arrays → List&lt;object&gt;, numbers → double).</summary>
    public static class GameGraphJsonReader
    {
        public static object Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int i = 0;
            try { object v = Value(json, ref i); return v; } catch (Exception) { return null; }
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("end");
            char c = s[i];
            if (c == '{')
            {
                var o = new JObj(); i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i); string k = Str(s, ref i); Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException(":"); i++;
                    object v = Value(s, ref i);
                    o.AddRaw(k, v);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("}");
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("]");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("\"");
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
    }
}
#endif
