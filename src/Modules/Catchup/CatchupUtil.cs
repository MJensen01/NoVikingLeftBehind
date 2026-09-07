using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Shared plumbing for the two catch-up modules (PlaytimeRubberBand, GroupSkillCatchup).
    /// Not a FeatureModule - static helpers only, so module auto-discovery ignores it.
    ///
    /// Contains:
    ///   * the on-disk location for our own state files,
    ///   * a tiny dependency-free JSON reader/writer (Mono's DataContractJsonSerializer is not
    ///     something we want to rely on inside Unity, and there is no Newtonsoft in the game),
    ///   * probabilistic rounding (so a x1.37 multiplier on a stack of 1 still pays out 37% of
    ///     the time instead of silently rounding to nothing),
    ///   * the shared [Catchup] SelfTest switch, bound exactly once by whichever module binds first.
    /// </summary>
    internal static class CatchupUtil
    {
        /// <summary>[Catchup] SelfTest - local, never synced. Bound once, shared by both modules.</summary>
        internal static ConfigEntry<bool> SelfTestCfg;

        internal static bool SelfTest => SelfTestCfg != null && SelfTestCfg.Value;

        /// <summary>
        /// Where our own state lives. Paths.ConfigPath is /config/bepinex in the server image,
        /// so this is /config/bepinex/nvlb - next to the config, never inside a world save.
        /// </summary>
        internal static string DataDir
        {
            get
            {
                string dir;
                try { dir = Path.Combine(Paths.ConfigPath, "nvlb"); }
                catch { dir = "nvlb"; }
                return dir;
            }
        }

        internal static string DataFile(string name)
        {
            return Path.Combine(DataDir, name);
        }

        internal static void EnsureDataDir()
        {
            var dir = DataDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>Seconds since the Unix epoch, UTC.</summary>
        internal static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>Steam id (socket host name) + character name - the identity we track playtime by.</summary>
        internal static string PeerKey(ZNetPeer peer)
        {
            if (peer == null) return null;
            string host = null;
            try { host = peer.m_socket != null ? peer.m_socket.GetHostName() : null; }
            catch { host = null; }
            if (string.IsNullOrEmpty(host)) host = "unknown";
            string name = peer.m_playerName;
            if (string.IsNullOrEmpty(name)) return null;   // not far enough through the handshake yet
            return host + "|" + name;
        }

        internal static string PeerHost(ZNetPeer peer)
        {
            try { return peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : "unknown"; }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Round a fractional amount without losing the fraction: 2.4 becomes 2 (60%) or 3 (40%).
        /// Keeps the long-run average exactly on the multiplier.
        /// </summary>
        internal static int ProbRound(float value)
        {
            if (value <= 0f) return 0;
            int whole = (int)value;
            float frac = value - whole;
            if (frac > 0f && UnityEngine.Random.value < frac) whole++;
            return whole;
        }

        internal static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        internal static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        // ---- minimal JSON ---------------------------------------------------------------

        internal static void AppendString(StringBuilder sb, string s)
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
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Parse JSON into Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null.
        /// Deliberately tiny: it only has to read files we wrote ourselves.
        /// </summary>
        internal static object ParseJson(string text)
        {
            int i = 0;
            var v = ParseValue(text, ref i);
            return v;
        }

        internal static Dictionary<string, object> AsObj(object o)
        {
            return o as Dictionary<string, object>;
        }

        internal static List<object> AsArr(object o)
        {
            return o as List<object>;
        }

        internal static string Str(Dictionary<string, object> o, string key, string def)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v is string) return (string)v;
            return def;
        }

        internal static double Num(Dictionary<string, object> o, string key, double def)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v is double) return (double)v;
            return def;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new Exception("unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (s.Length - i >= 4 && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (s.Length - i >= 5 && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (s.Length - i >= 4 && s.Substring(i, 4) == "null") { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var o = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new Exception("expected ':' at " + i);
                i++;
                o[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new Exception("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new Exception("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new Exception("expected string at " + i);
            i++;
            var sb = new StringBuilder();
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
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new Exception("unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' ||
                                    s[i] == 'e' || s[i] == 'E')) i++;
            if (start == i) throw new Exception("expected number at " + i);
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }
    }
}
