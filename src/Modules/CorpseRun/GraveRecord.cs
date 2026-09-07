using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Where the grave is, according to US - not according to the character profile.
    ///
    /// THE BUG THIS EXISTS TO FIX (0.4.2): CorpseRunPlus used to gate the compass, GravePull and
    /// CorpseRunScaled on <c>PlayerProfile.HaveDeathPoint()</c> / <c>GetDeathPoint()</c>. Those two
    /// live in the character's .fch and vanilla NEVER clears them: once a character has died
    /// anywhere, ever - in any world - HaveDeathPoint() is true forever. So joining a fresh world
    /// with an old character lit the compass immediately and pointed it at a death spot that might
    /// belong to a different world entirely, with GravePull buffing the whole run.
    ///
    /// The record here replaces that. It is written on the local player's own death, stored in
    /// <c>Player.m_customData["nvlb.grave"]</c> - the same Dictionary&lt;string,string&gt; the extra
    /// slots use (vanilla persists it verbatim in Player.Save for save version >= 26), so it
    /// survives a relog with an unlooted grave, which is the case that has to keep working - and it
    /// carries the WORLD NAME, so it is simply ignored in any other world. It is deleted when the
    /// grave is looted or emptied, and by the <c>nvlb.grave.clear</c> console command.
    ///
    /// Deliberately NOT done: confirming a TombStone ZDO still exists near the recorded point.
    /// ZDOMan only knows about zones that are loaded, and a grave you are running back to is by
    /// definition far away and unloaded, so the check would report "no grave" exactly when the
    /// compass matters most. Loot/empty clearing plus the world-name match covers the real cases.
    ///
    /// Format: <c>1|&lt;world&gt;|&lt;x&gt;|&lt;y&gt;|&lt;z&gt;|&lt;gameTimeSeconds&gt;</c>, invariant
    /// culture. The world name is written last-but-one on purpose: it is the only field that may
    /// contain a '|' (a world name is user-chosen), so it is escaped rather than split blindly.
    /// </summary>
    internal static class GraveRecord
    {
        public const string Key = "nvlb.grave";
        private const int Version = 1;

        /// <summary>A world name may contain anything, including our separator.</summary>
        private static string Esc(string s) { return (s ?? "").Replace("|", "%7C"); }
        private static string Unesc(string s) { return (s ?? "").Replace("%7C", "|"); }

        // ---- pure encoding (no game state - this is what the self test exercises) ---------------

        public static string Encode(string world, Vector3 pos, double gameTime)
        {
            var c = CultureInfo.InvariantCulture;
            return Version + "|" + Esc(world) + "|" +
                   pos.x.ToString("R", c) + "|" + pos.y.ToString("R", c) + "|" + pos.z.ToString("R", c) + "|" +
                   gameTime.ToString("R", c);
        }

        /// <summary>Parse a record string. Returns false (never throws) on anything unexpected.</summary>
        public static bool TryDecode(string s, out string world, out Vector3 pos, out double gameTime)
        {
            world = null; pos = Vector3.zero; gameTime = 0d;
            if (string.IsNullOrEmpty(s)) return false;
            try
            {
                var p = s.Split('|');
                if (p.Length != 6) return false;
                int ver;
                if (!int.TryParse(p[0], out ver) || ver != Version) return false;
                var c = CultureInfo.InvariantCulture;
                float x, y, z; double t;
                if (!float.TryParse(p[2], NumberStyles.Float, c, out x)) return false;
                if (!float.TryParse(p[3], NumberStyles.Float, c, out y)) return false;
                if (!float.TryParse(p[4], NumberStyles.Float, c, out z)) return false;
                if (!double.TryParse(p[5], NumberStyles.Float, c, out t)) return false;
                world = Unesc(p[1]);
                pos = new Vector3(x, y, z);
                gameTime = t;
                return true;
            }
            catch { return false; }
        }

        // ---- dictionary level (still no game state, so the self test can drive it) ---------------

        public static void Write(Dictionary<string, string> data, string world, Vector3 pos, double gameTime)
        {
            if (data == null) return;
            data[Key] = Encode(world, pos, gameTime);
        }

        /// <summary>
        /// Read the record back, but only when it belongs to <paramref name="currentWorld"/>. A
        /// record from another world is left in place (that world's grave may still be unlooted)
        /// and simply reported as absent here.
        /// </summary>
        public static bool TryRead(Dictionary<string, string> data, string currentWorld,
                                   out Vector3 pos, out double gameTime, out string recordWorld)
        {
            pos = Vector3.zero; gameTime = 0d; recordWorld = null;
            if (data == null) return false;
            string raw;
            if (!data.TryGetValue(Key, out raw)) return false;
            string w;
            if (!TryDecode(raw, out w, out pos, out gameTime)) return false;
            recordWorld = w;
            if (!string.Equals(w, currentWorld ?? "", StringComparison.Ordinal)) { pos = Vector3.zero; return false; }
            return true;
        }

        public static bool Clear(Dictionary<string, string> data)
        {
            return data != null && data.Remove(Key);
        }

        public static bool Has(Dictionary<string, string> data)
        {
            return data != null && data.ContainsKey(Key);
        }

        // ---- live game wrappers ------------------------------------------------------------------

        /// <summary>The world we are in right now, or "" when ZNet is not up yet.</summary>
        public static string CurrentWorld()
        {
            try { return ZNet.instance != null ? (ZNet.instance.GetWorldName() ?? "") : ""; }
            catch { return ""; }
        }

        public static double GameTime()
        {
            try { return ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0d; }
            catch { return 0d; }
        }

        public static void Write(Player p, Vector3 pos)
        {
            if (p == null) return;
            if (p.m_customData == null) p.m_customData = new Dictionary<string, string>();
            Write(p.m_customData, CurrentWorld(), pos, GameTime());
        }

        public static bool TryRead(Player p, out Vector3 pos, out double gameTime, out string recordWorld)
        {
            pos = Vector3.zero; gameTime = 0d; recordWorld = null;
            return p != null && TryRead(p.m_customData, CurrentWorld(), out pos, out gameTime, out recordWorld);
        }

        public static bool Clear(Player p)
        {
            return p != null && Clear(p.m_customData);
        }

        /// <summary>One line for nvlb.status: what the record says, whatever world it is from.</summary>
        public static string Describe(Player p)
        {
            if (p == null || p.m_customData == null) return "none";
            string raw;
            if (!p.m_customData.TryGetValue(Key, out raw)) return "none";
            string w; Vector3 pos; double t;
            if (!TryDecode(raw, out w, out pos, out t)) return "unreadable";
            string here = CurrentWorld();
            return pos.ToString("F0") + " in '" + w + "'" +
                   (string.Equals(w, here, StringComparison.Ordinal) ? "" : " (OTHER world, ignored here: '" + here + "')");
        }
    }
}
