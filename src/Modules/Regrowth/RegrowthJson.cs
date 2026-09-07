using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// A ~100-line reader/writer for exactly one file shape: regrowth.json.
    ///
    /// Why not JsonUtility: it lives in UnityEngine.JSONSerializeModule, which NoVikingLeftBehind.csproj
    /// does not reference, and the csproj is shared with three other module authors this session
    /// - adding a reference there would collide. Why not Newtonsoft: not guaranteed present in
    /// the server's BepInEx. The schema is a flat array of flat records, so a purpose-built
    /// parser is smaller than either dependency and cannot surprise us.
    ///
    /// Shape:
    /// { "entries": [ { "prefabHash":123, "name":"rock4_copper", "tier":1, "day":5,
    ///                  "x":1.0, "y":2.0, "z":3.0, "rx":0.0, "ry":0.0, "rz":0.0 }, ... ] }
    /// Unknown keys are ignored; a malformed record is skipped, not fatal.
    /// </summary>
    internal static class RegrowthJson
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Write(List<RegrowthEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"entries\": [\n");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("    {");
                sb.Append("\"prefabHash\":").Append(e.prefabHash.ToString(Inv));
                sb.Append(",\"name\":\"").Append(Escape(e.name)).Append('"');
                sb.Append(",\"tier\":").Append(e.tier.ToString(Inv));
                sb.Append(",\"day\":").Append(e.day.ToString(Inv));
                sb.Append(",\"x\":").Append(F(e.x));
                sb.Append(",\"y\":").Append(F(e.y));
                sb.Append(",\"z\":").Append(F(e.z));
                sb.Append(",\"rx\":").Append(F(e.rx));
                sb.Append(",\"ry\":").Append(F(e.ry));
                sb.Append(",\"rz\":").Append(F(e.rz));
                sb.Append('}');
                if (i < entries.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        public static List<RegrowthEntry> Read(string json)
        {
            var result = new List<RegrowthEntry>();
            if (string.IsNullOrEmpty(json)) return result;

            int i = 0;
            while (i < json.Length)
            {
                int open = json.IndexOf('{', i);
                if (open < 0) break;
                // skip the outer wrapper object (the one whose first non-space char is a quote
                // followed by "entries") by only accepting objects that carry "prefabHash".
                int close = MatchBrace(json, open);
                if (close < 0) break;
                var body = json.Substring(open + 1, close - open - 1);
                if (body.IndexOf("\"prefabHash\"", StringComparison.Ordinal) >= 0 &&
                    body.IndexOf('{') < 0)
                {
                    var e = ReadEntry(body);
                    if (e != null) result.Add(e);
                    i = close + 1;
                }
                else
                {
                    i = open + 1;   // descend into the wrapper
                }
            }
            return result;
        }

        private static RegrowthEntry ReadEntry(string body)
        {
            try
            {
                var e = new RegrowthEntry
                {
                    prefabHash = (int)Num(body, "prefabHash"),
                    name = Str(body, "name"),
                    tier = (int)Num(body, "tier"),
                    day = (int)Num(body, "day"),
                    x = Num(body, "x"), y = Num(body, "y"), z = Num(body, "z"),
                    rx = Num(body, "rx"), ry = Num(body, "ry"), rz = Num(body, "rz")
                };
                return e.prefabHash == 0 ? null : e;
            }
            catch { return null; }
        }

        private static int MatchBrace(string s, int open)
        {
            int depth = 0; bool inStr = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>Value of "key": as a number; 0 when absent.</summary>
        private static float Num(string body, string key)
        {
            int p = ValueStart(body, key);
            if (p < 0) return 0f;
            int end = p;
            while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '-' || body[end] == '+' ||
                                         body[end] == '.' || body[end] == 'e' || body[end] == 'E')) end++;
            float v;
            return float.TryParse(body.Substring(p, end - p), NumberStyles.Float, Inv, out v) ? v : 0f;
        }

        /// <summary>Value of "key": as a string; "" when absent.</summary>
        private static string Str(string body, string key)
        {
            int p = ValueStart(body, key);
            if (p < 0 || p >= body.Length || body[p] != '"') return "";
            var sb = new StringBuilder();
            for (int i = p + 1; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '\\' && i + 1 < body.Length) { sb.Append(body[++i]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int ValueStart(string body, string key)
        {
            int k = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return -1;
            int c = body.IndexOf(':', k + key.Length + 2);
            if (c < 0) return -1;
            c++;
            while (c < body.Length && char.IsWhiteSpace(body[c])) c++;
            return c;
        }

        private static string F(float v) { return v.ToString("R", Inv); }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
