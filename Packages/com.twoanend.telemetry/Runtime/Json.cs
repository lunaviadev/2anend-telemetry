using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TwoAnEnd.Playtest
{
    /// A value that's already JSON (e.g. from JsonUtility.ToJson), written as it is.
    public readonly struct RawJson
    {
        public readonly string json;
        public RawJson(string json) { this.json = json; }
    }

    /// A small JSON writer, enough for telemetry events, with no dependencies. Numbers are
    /// written culture-invariant (a German PC still writes 0.5, not 0,5), NaN and infinity
    /// become null, and strings are escaped so a line can never break the JSONL file.
    public static class Json
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Write(object value)
        {
            var sb = new StringBuilder(128);
            Write(sb, value);
            return sb.ToString();
        }

        public static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: Str(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case float f: Num(sb, f, "0.####"); return;
                case double d: Num(sb, d, "0.######"); return;
                case int i: sb.Append(i.ToString(Inv)); return;
                case long l: sb.Append(l.ToString(Inv)); return;
                case short or ushort or uint or ulong or byte or sbyte or decimal: sb.Append(Convert.ToString(v, Inv)); return;
                case Enum e: Str(sb, e.ToString()); return;
                case RawJson r: sb.Append(string.IsNullOrEmpty(r.json) ? "null" : r.json); return;
                case Vector2 v2: Arr(sb, v2.x, v2.y); return;
                case Vector3 v3: Arr(sb, v3.x, v3.y, v3.z); return;
                case Color c: Str(sb, "#" + ColorUtility.ToHtmlStringRGBA(c)); return;
                case IDictionary<string, object> d: Obj(sb, d); return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Str(sb, Convert.ToString(kv.Key, Inv));
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    return;
                }
                default: Str(sb, Convert.ToString(v, Inv)); return;
            }
        }

        public static void Obj(StringBuilder sb, IDictionary<string, object> d)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                Str(sb, kv.Key);
                sb.Append(':');
                Write(sb, kv.Value);
            }
            sb.Append('}');
        }

        private static void Arr(StringBuilder sb, params float[] xs)
        {
            sb.Append('[');
            for (int i = 0; i < xs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                Num(sb, xs[i], "0.####");
            }
            sb.Append(']');
        }

        private static void Num(StringBuilder sb, double d, string fmt)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(d.ToString(fmt, Inv));
        }

        public static void Str(StringBuilder sb, string s)
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
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append('\\').Append('u').Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// "key", value, "key", value... into a dictionary, keeping the order they were given in.
        public static Dictionary<string, object> Pairs(object[] kv)
        {
            var d = new Dictionary<string, object>();
            if (kv == null) return d;
            if (kv.Length == 1 && kv[0] is IDictionary<string, object> one)
            {
                foreach (var p in one) d[p.Key] = p.Value;
                return d;
            }
            for (int i = 0; i < kv.Length; i += 2)
            {
                string key = kv[i] as string ?? Convert.ToString(kv[i], Inv) ?? "_";
                d[key] = i + 1 < kv.Length ? kv[i + 1] : null;
            }
            return d;
        }
    }
}
