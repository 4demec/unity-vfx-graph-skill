// VfxAiJson.cs
// Minimal dependency-free JSON writer/reader used by the VFX AI kernel.
// Compiled into Unity.VisualEffectGraph.Editor via VFXAI.Kernel.asmref.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VfxAi.Kernel
{
    /// <summary>Tiny JSON writer with an explicit token state machine.</summary>
    public sealed class JsonWriter
    {
        readonly StringBuilder m_Sb = new StringBuilder();
        readonly bool m_Pretty;
        int m_Indent;
        bool m_NeedComma;
        bool m_AfterKey;

        public JsonWriter(bool pretty = false) { m_Pretty = pretty; }

        void NewLine()
        {
            if (!m_Pretty) return;
            if (m_Sb.Length == 0) return;
            m_Sb.Append('\n').Append(' ', m_Indent * 2);
        }

        void Pre()
        {
            if (m_AfterKey) m_AfterKey = false;
            else
            {
                if (m_NeedComma) m_Sb.Append(',');
                NewLine();
            }
            m_NeedComma = true;
        }

        public JsonWriter BeginObject() { Pre(); m_Sb.Append('{'); m_NeedComma = false; m_Indent++; return this; }
        public JsonWriter EndObject() { m_Indent--; NewLine(); m_Sb.Append('}'); m_NeedComma = true; return this; }
        public JsonWriter BeginArray() { Pre(); m_Sb.Append('['); m_NeedComma = false; m_Indent++; return this; }
        public JsonWriter EndArray() { m_Indent--; NewLine(); m_Sb.Append(']'); m_NeedComma = true; return this; }

        public JsonWriter Key(string k)
        {
            if (m_NeedComma) m_Sb.Append(',');
            NewLine();
            m_NeedComma = false;
            m_Sb.Append(Quote(k)).Append(':');
            if (m_Pretty) m_Sb.Append(' ');
            m_AfterKey = true;
            return this;
        }

        public JsonWriter Value(string v) { Pre(); m_Sb.Append(v == null ? "null" : Quote(v)); return this; }
        public JsonWriter Value(bool v) { Pre(); m_Sb.Append(v ? "true" : "false"); return this; }
        public JsonWriter Value(int v) { Pre(); m_Sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(long v) { Pre(); m_Sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

        public JsonWriter Value(float v)
        {
            Pre();
            if (float.IsNaN(v) || float.IsInfinity(v)) m_Sb.Append("null");
            else m_Sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(double v)
        {
            Pre();
            if (double.IsNaN(v) || double.IsInfinity(v)) m_Sb.Append("null");
            else m_Sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Null() { Pre(); m_Sb.Append("null"); return this; }
        public JsonWriter Raw(string json) { Pre(); m_Sb.Append(string.IsNullOrEmpty(json) ? "null" : json); return this; }

        public JsonWriter Prop(string k, string v) { return Key(k).Value(v); }
        public JsonWriter Prop(string k, bool v) { return Key(k).Value(v); }
        public JsonWriter Prop(string k, int v) { return Key(k).Value(v); }
        public JsonWriter Prop(string k, float v) { return Key(k).Value(v); }

        public JsonWriter BeginObject(string k) { return Key(k).BeginObject(); }
        public JsonWriter BeginArray(string k) { return Key(k).BeginArray(); }

        public override string ToString() { return m_Sb.ToString(); }

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Tiny recursive-descent JSON parser. Produces Dictionary&lt;string,object&gt;, List&lt;object&gt;,
    /// string, double, bool and null.
    /// </summary>
    public static class JsonReader
    {
        public static object Parse(string text)
        {
            int i = 0;
            var v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("Trailing characters at index " + i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("Expected '" + word + "' at index " + i);
            i += word.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected object key at index " + i);
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at index " + i);
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("Expected ',' or '}' at index " + i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("Expected ',' or ']' at index " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException("Bad escape");
                }
            }
            throw new FormatException("Unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            var span = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("Bad number '" + span + "' at index " + start);
            return d;
        }

        // ---- typed accessors -------------------------------------------------

        public static Dictionary<string, object> AsObject(object o) { return o as Dictionary<string, object>; }
        public static List<object> AsArray(object o) { return o as List<object>; }

        public static string GetString(Dictionary<string, object> o, string key, string fallback = null)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool fallback = false)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0.0;
            bool parsed;
            return bool.TryParse(v.ToString(), out parsed) ? parsed : fallback;
        }

        public static float GetFloat(Dictionary<string, object> o, string key, float fallback = 0f)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (float)(double)v;
            float f;
            return float.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback;
        }

        public static int GetInt(Dictionary<string, object> o, string key, int fallback = 0)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (int)Math.Round((double)v);
            int n;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        public static List<object> GetArray(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }
    }
}
