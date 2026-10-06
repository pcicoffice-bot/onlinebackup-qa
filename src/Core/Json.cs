using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OnlineBackup.Core
{
    /// <summary>
    /// A small JSON reader / writer for .NET 4.0 (no System.Text.Json there): objects → Dictionary, arrays → List,
    /// numbers → double, true/false → bool, null → null. Enough for Microsoft Graph and restic's --json output.
    /// </summary>
    public static class Json
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); Ws(s, ref i); if (i != s.Length) throw new FormatException("JSON: text after the value"); return v; }

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static List<object> Arr(object o) { return o as List<object> ?? new List<object>(); }
        public static string Str(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
        public static long Num(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v is double ? (long)(double)v : 0; }
        public static Dictionary<string, object> Child(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal); i++; Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: ':' expected"); i++;
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("JSON: ',' or '}' expected");
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("JSON: ',' or ']' expected");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("JSON: unexpected '" + c + "'");
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON: string expected");
            i++; var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break; case 't': sb.Append('\t'); break; case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON: unterminated string");
        }

        /// <summary>Writes a string / number / bool / null / dictionary / list as JSON.</summary>
        public static string Write(object v)
        {
            var sb = new StringBuilder(); W(sb, v); return sb.ToString();
        }

        static void W(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is string) { Q(sb, (string)v); return; }
            if (v is int || v is long || v is double) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            var d = v as IDictionary<string, object>;
            if (d != null) { sb.Append('{'); bool f = true; foreach (var kv in d) { if (!f) sb.Append(','); f = false; Q(sb, kv.Key); sb.Append(':'); W(sb, kv.Value); } sb.Append('}'); return; }
            var l = v as System.Collections.IEnumerable;
            if (l != null) { sb.Append('['); bool f = true; foreach (var x in l) { if (!f) sb.Append(','); f = false; W(sb, x); } sb.Append(']'); return; }
            Q(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
