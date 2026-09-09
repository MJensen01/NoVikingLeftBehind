using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// One recorded grave: where it is, how much was in it, and (when we could get it) the ZDOID
    /// of the tombstone itself so looting can be matched to the right record even if the stone has
    /// drifted (TombStone.PositionCheck allows 4 m of float before it snaps back).
    ///
    /// <see cref="Items"/> is the number of item stacks that actually went INTO the tombstone,
    /// counted off the tombstone's own Container the moment vanilla finishes building it
    /// (TombStone.Setup, called at the end of Player.CreateTombStone once
    /// Inventory.MoveInventoryToGrave has run). -1 means "unknown", which today only happens to a
    /// record migrated from the pre-0.9.1 single-grave format, or if the tombstone hand-off failed;
    /// it is never filtered out and ranks as 1 item so it cannot silently outrank a real grave.
    /// </summary>
    internal sealed class Grave
    {
        public string World = "";
        public Vector3 Pos;
        public double Time;
        public int Items = -1;
        public long ZdoUser;
        public uint ZdoId;

        public bool ItemsKnown { get { return Items >= 0; } }

        /// <summary>What "richest grave" compares. An unknown count is worth exactly one item.</summary>
        public int RankItems { get { return Items < 0 ? 1 : Items; } }

        public bool HasZdo { get { return ZdoUser != 0L || ZdoId != 0u; } }

        /// <summary>
        /// Stable identity of this entry, used to hold a manual selection across the per-frame
        /// re-read of the record. World + death time is already unique (you cannot die twice in
        /// the same world at the same game second); the rounded position is in it so a hand-edited
        /// record cannot collide.
        /// </summary>
        public string Id
        {
            get
            {
                var c = CultureInfo.InvariantCulture;
                return World + "@" + Time.ToString("R", c) + "@" +
                       Mathf.RoundToInt(Pos.x).ToString(c) + "," + Mathf.RoundToInt(Pos.z).ToString(c);
            }
        }

        public string ItemsText
        {
            get { return ItemsKnown ? (Items + (Items == 1 ? " item" : " items")) : "? items"; }
        }

        public string Describe()
        {
            return "(" + Mathf.RoundToInt(Pos.x) + ", " + Mathf.RoundToInt(Pos.z) + ") " +
                   ItemsText + " in '" + World + "'";
        }
    }

    /// <summary>
    /// Where our graves are, according to US - not according to the character profile.
    ///
    /// THE BUG THIS EXISTS TO FIX (0.4.2): CorpseRunPlus used to gate the compass, GravePull and
    /// CorpseRunScaled on <c>PlayerProfile.HaveDeathPoint()</c> / <c>GetDeathPoint()</c>. Those two
    /// live in the character's .fch and vanilla NEVER clears them: once a character has died
    /// anywhere, ever - in any world - HaveDeathPoint() is true forever. So joining a fresh world
    /// with an old character lit the compass immediately and pointed it at a death spot that might
    /// belong to a different world entirely, with GravePull buffing the whole run.
    ///
    /// THE BUG 0.9.1 FIXES: the record used to hold exactly ONE grave and the newest death always
    /// won. Die on the way back to a 30-item grave with two mushrooms on you and the compass
    /// swung to the near-empty second grave, hiding the one that mattered. The record is now a
    /// LIST (<see cref="MaxPerWorld"/> per world, oldest evicted first) and the module tracks the
    /// grave with the MOST items, ties going to the most recent; when that one is looted or
    /// dismissed, tracking falls through to the next best.
    ///
    /// It is written on the local player's own death, stored in
    /// <c>Player.m_customData["nvlb.grave"]</c> - the same Dictionary&lt;string,string&gt; the extra
    /// slots use (vanilla persists it verbatim in Player.Save for save version >= 26), so it
    /// survives a relog with an unlooted grave, which is the case that has to keep working - and
    /// every entry carries the WORLD NAME, so graves belonging to other worlds are simply ignored
    /// here and left alone. An entry is deleted when its own grave is looted or emptied, and by the
    /// <c>nvlb.grave.clear</c> console command.
    ///
    /// Deliberately NOT done: confirming a TombStone ZDO still exists near a recorded point.
    /// ZDOMan only knows about zones that are loaded, and a grave you are running back to is by
    /// definition far away and unloaded, so the check would report "no grave" exactly when the
    /// compass matters most. Loot/empty clearing plus the world-name match covers the real cases.
    ///
    /// FORMAT (invariant culture throughout):
    ///   v2 (current)  <c>2;&lt;entry&gt;;&lt;entry&gt;;...</c>
    ///                 entry = <c>world|x|y|z|gameTime|items|zdoUser|zdoId</c>
    ///   v1 (0.4.2 - 0.9.0, still read)  <c>1|&lt;world&gt;|&lt;x&gt;|&lt;y&gt;|&lt;z&gt;|&lt;gameTime&gt;</c>
    ///
    /// A v1 record decodes to a single entry with an unknown item count, so nobody loses a grave
    /// marker on update. The world name is the only field that can contain a separator (it is
    /// user-chosen), so it is escaped: '%' -> %25 first, then '|' -> %7C and ';' -> %3B.
    /// </summary>
    internal static class GraveRecord
    {
        public const string Key = "nvlb.grave";
        private const int Version = 2;

        /// <summary>How many unlooted graves are remembered per world. Oldest is evicted first.</summary>
        public const int MaxPerWorld = 5;

        /// <summary>Belt and braces on the size of the string that ends up in the .fch.</summary>
        public const int MaxTotal = 20;

        // ---- escaping ---------------------------------------------------------------------------

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("%", "%25").Replace("|", "%7C").Replace(";", "%3B");
        }

        private static string Unesc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("%7C", "|").Replace("%3B", ";").Replace("%25", "%");
        }

        // ---- pure encoding (no game state - this is what the self test exercises) ---------------

        public static string Encode(List<Grave> graves)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(Version);
            if (graves != null)
            {
                for (int i = 0; i < graves.Count; i++)
                {
                    var g = graves[i];
                    if (g == null) continue;
                    sb.Append(';')
                      .Append(Esc(g.World)).Append('|')
                      .Append(g.Pos.x.ToString("R", c)).Append('|')
                      .Append(g.Pos.y.ToString("R", c)).Append('|')
                      .Append(g.Pos.z.ToString("R", c)).Append('|')
                      .Append(g.Time.ToString("R", c)).Append('|')
                      .Append(g.Items.ToString(c)).Append('|')
                      .Append(g.ZdoUser.ToString(c)).Append('|')
                      .Append(g.ZdoId.ToString(c));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parse a record string, v1 or v2. Returns an empty list (never throws, never null) on
        /// anything unexpected; a single unreadable entry is skipped rather than losing the rest.
        /// </summary>
        public static List<Grave> Decode(string s)
        {
            var list = new List<Grave>();
            if (string.IsNullOrEmpty(s)) return list;
            try
            {
                var c = CultureInfo.InvariantCulture;

                // v1: one record, '|' separated, no ';' anywhere.
                if (s.Length > 1 && s[0] == '1' && s[1] == '|')
                {
                    var p = s.Split('|');
                    if (p.Length != 6) return list;
                    float x1, y1, z1; double t1;
                    if (!float.TryParse(p[2], NumberStyles.Float, c, out x1)) return list;
                    if (!float.TryParse(p[3], NumberStyles.Float, c, out y1)) return list;
                    if (!float.TryParse(p[4], NumberStyles.Float, c, out z1)) return list;
                    if (!double.TryParse(p[5], NumberStyles.Float, c, out t1)) return list;
                    var old = new Grave();
                    old.World = (p[1] ?? "").Replace("%7C", "|");   // v1 escaped only '|'
                    old.Pos = new Vector3(x1, y1, z1);
                    old.Time = t1;
                    old.Items = -1;                                  // unknown, and never filtered
                    list.Add(old);
                    return list;
                }

                var parts = s.Split(';');
                int ver;
                if (!int.TryParse(parts[0], NumberStyles.Integer, c, out ver) || ver != Version) return list;

                for (int i = 1; i < parts.Length; i++)
                {
                    var f = parts[i].Split('|');
                    if (f.Length < 6) continue;
                    float x, y, z; double t; int items;
                    if (!float.TryParse(f[1], NumberStyles.Float, c, out x)) continue;
                    if (!float.TryParse(f[2], NumberStyles.Float, c, out y)) continue;
                    if (!float.TryParse(f[3], NumberStyles.Float, c, out z)) continue;
                    if (!double.TryParse(f[4], NumberStyles.Float, c, out t)) continue;
                    if (!int.TryParse(f[5], NumberStyles.Integer, c, out items)) items = -1;

                    var g = new Grave();
                    g.World = Unesc(f[0]);
                    g.Pos = new Vector3(x, y, z);
                    g.Time = t;
                    g.Items = items;
                    long zu; uint zi;
                    if (f.Length > 6 && long.TryParse(f[6], NumberStyles.Integer, c, out zu)) g.ZdoUser = zu;
                    if (f.Length > 7 && uint.TryParse(f[7], NumberStyles.Integer, c, out zi)) g.ZdoId = zi;
                    list.Add(g);
                }
            }
            catch { list.Clear(); }
            return list;
        }

        // ---- dictionary level (still no game state, so the self test can drive it) ---------------

        /// <summary>The raw stored string, or null. Used as a cheap cache key by the tick.</summary>
        public static string Raw(Dictionary<string, string> data)
        {
            string raw;
            if (data == null || !data.TryGetValue(Key, out raw)) return null;
            return raw;
        }

        /// <summary>Every recorded grave, in the order they were recorded (oldest first).</summary>
        public static List<Grave> ReadAll(Dictionary<string, string> data)
        {
            return Decode(Raw(data));
        }

        /// <summary>
        /// The graves that belong to <paramref name="world"/>, ranked: most items first, ties going
        /// to the most recent death. Index 0 is what the compass points at unless the player has
        /// cycled by hand. Records from other worlds are not returned and not touched.
        /// </summary>
        public static List<Grave> ReadWorld(Dictionary<string, string> data, string world)
        {
            return RankWorld(ReadAll(data), world);
        }

        public static List<Grave> RankWorld(List<Grave> all, string world)
        {
            var mine = new List<Grave>();
            if (all != null)
                foreach (var g in all)
                    if (g != null && string.Equals(g.World, world ?? "", StringComparison.Ordinal)) mine.Add(g);
            Rank(mine);
            return mine;
        }

        /// <summary>Most items first; ties broken by the most recent death. Stable enough to cycle.</summary>
        public static void Rank(List<Grave> list)
        {
            if (list == null) return;
            list.Sort(delegate (Grave a, Grave b)
            {
                int byItems = b.RankItems.CompareTo(a.RankItems);
                if (byItems != 0) return byItems;
                int byTime = b.Time.CompareTo(a.Time);
                if (byTime != 0) return byTime;
                return string.CompareOrdinal(a.Id, b.Id);
            });
        }

        public static void Write(Dictionary<string, string> data, List<Grave> all)
        {
            if (data == null) return;
            if (all == null || all.Count == 0) { data.Remove(Key); return; }
            data[Key] = Encode(all);
        }

        /// <summary>
        /// Record a grave. Returns false - and writes nothing at all - when
        /// <paramref name="items"/> is below <paramref name="minItems"/> (a "basically nothing"
        /// death must never displace the grave that matters), or when it is 0 (vanilla does not
        /// even spawn a tombstone for an empty inventory: Player.CreateTombStone:3290).
        /// An unknown count (-1) is always recorded.
        ///
        /// The per-world cap is enforced oldest-out, and a global cap keeps the string that ends
        /// up in the .fch small however many worlds a character has died in.
        /// </summary>
        public static bool Add(Dictionary<string, string> data, string world, Vector3 pos, double gameTime,
                               int items, long zdoUser, uint zdoId, int minItems,
                               out Grave added, out List<Grave> evicted)
        {
            added = null;
            evicted = new List<Grave>();
            if (data == null) return false;
            if (items == 0) return false;
            if (items > 0 && items < (minItems < 1 ? 1 : minItems)) return false;

            var all = ReadAll(data);

            added = new Grave();
            added.World = world ?? "";
            added.Pos = pos;
            added.Time = gameTime;
            added.Items = items;
            added.ZdoUser = zdoUser;
            added.ZdoId = zdoId;
            all.Add(added);

            // Per-world cap, oldest first. all is in record order, so a forward scan finds the
            // oldest entries of this world first.
            int inWorld = 0;
            foreach (var g in all)
                if (string.Equals(g.World, added.World, StringComparison.Ordinal)) inWorld++;
            while (inWorld > MaxPerWorld)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (!string.Equals(all[i].World, added.World, StringComparison.Ordinal)) continue;
                    evicted.Add(all[i]);
                    all.RemoveAt(i);
                    inWorld--;
                    break;
                }
            }

            // Global cap, oldest first, so a character that has died in a dozen worlds cannot grow
            // the custom-data string without bound.
            while (all.Count > MaxTotal)
            {
                evicted.Add(all[0]);
                all.RemoveAt(0);
            }

            Write(data, all);
            return true;
        }

        /// <summary>
        /// The recorded grave a touched tombstone belongs to: an exact ZDOID match wins outright,
        /// otherwise the NEAREST record of this world within <paramref name="radius"/> metres.
        /// Null when the stone is not one of ours (somebody else's grave, or one we never recorded).
        /// </summary>
        public static Grave Match(List<Grave> worldGraves, Vector3 stonePos, long zdoUser, uint zdoId, float radius)
        {
            if (worldGraves == null || worldGraves.Count == 0) return null;

            if (zdoUser != 0L || zdoId != 0u)
                foreach (var g in worldGraves)
                    if (g.HasZdo && g.ZdoUser == zdoUser && g.ZdoId == zdoId) return g;

            Grave best = null;
            float bestD = float.MaxValue;
            foreach (var g in worldGraves)
            {
                float d = Vector3.Distance(g.Pos, stonePos);
                if (d <= radius && d < bestD) { bestD = d; best = g; }
            }
            return best;
        }

        /// <summary>Delete one entry, identified by <see cref="Grave.Id"/>. True if it was there.</summary>
        public static bool Remove(Dictionary<string, string> data, string id)
        {
            if (data == null || string.IsNullOrEmpty(id)) return false;
            var all = ReadAll(data);
            for (int i = 0; i < all.Count; i++)
            {
                if (!string.Equals(all[i].Id, id, StringComparison.Ordinal)) continue;
                all.RemoveAt(i);
                Write(data, all);
                return true;
            }
            return false;
        }

        /// <summary>Delete every entry of one world. Returns how many went. Other worlds are kept.</summary>
        public static int ClearWorld(Dictionary<string, string> data, string world)
        {
            if (data == null) return 0;
            var all = ReadAll(data);
            int before = all.Count;
            all.RemoveAll(delegate (Grave g) { return string.Equals(g.World, world ?? "", StringComparison.Ordinal); });
            if (all.Count == before) return 0;
            Write(data, all);
            return before - all.Count;
        }

        public static bool ClearAll(Dictionary<string, string> data)
        {
            return data != null && data.Remove(Key);
        }

        public static bool Has(Dictionary<string, string> data)
        {
            return data != null && data.ContainsKey(Key);
        }

        public static Grave ById(List<Grave> list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return null;
            foreach (var g in list) if (string.Equals(g.Id, id, StringComparison.Ordinal)) return g;
            return null;
        }

        /// <summary>The grave the compass points at by default: most items, ties to the newest.</summary>
        public static Grave Best(List<Grave> ranked)
        {
            return ranked != null && ranked.Count > 0 ? ranked[0] : null;
        }

        /// <summary>
        /// The next grave in rank order, wrapping. An id we no longer hold (it was just looted)
        /// starts again at the best one, which is what falling through should feel like.
        /// </summary>
        public static Grave Next(List<Grave> ranked, string currentId)
        {
            if (ranked == null || ranked.Count == 0) return null;
            for (int i = 0; i < ranked.Count; i++)
                if (string.Equals(ranked[i].Id, currentId, StringComparison.Ordinal))
                    return ranked[(i + 1) % ranked.Count];
            return ranked[0];
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

        private static Dictionary<string, string> DataOf(Player p)
        {
            if (p == null) return null;
            if (p.m_customData == null) p.m_customData = new Dictionary<string, string>();
            return p.m_customData;
        }

        public static string Raw(Player p) { return p == null ? null : Raw(p.m_customData); }

        public static List<Grave> ReadWorld(Player p)
        {
            return p == null ? new List<Grave>() : ReadWorld(p.m_customData, CurrentWorld());
        }

        public static List<Grave> ReadAll(Player p)
        {
            return p == null ? new List<Grave>() : ReadAll(p.m_customData);
        }

        public static bool Add(Player p, Vector3 pos, int items, long zdoUser, uint zdoId, int minItems,
                               out Grave added, out List<Grave> evicted)
        {
            added = null; evicted = new List<Grave>();
            var data = DataOf(p);
            if (data == null) return false;
            return Add(data, CurrentWorld(), pos, GameTime(), items, zdoUser, zdoId, minItems,
                       out added, out evicted);
        }

        public static bool Remove(Player p, string id) { return p != null && Remove(p.m_customData, id); }
        public static int ClearWorld(Player p) { return p == null ? 0 : ClearWorld(p.m_customData, CurrentWorld()); }
        public static bool ClearAll(Player p) { return p != null && ClearAll(p.m_customData); }

        /// <summary>One line for nvlb.status: what the record says, whatever world it is from.</summary>
        public static string Describe(Player p)
        {
            if (p == null || p.m_customData == null) return "none";
            string raw = Raw(p.m_customData);
            if (raw == null) return "none";
            var all = Decode(raw);
            if (all.Count == 0) return "unreadable";
            string here = CurrentWorld();
            var mine = RankWorld(all, here);
            int elsewhere = all.Count - mine.Count;
            if (mine.Count == 0)
                return "0 here, " + elsewhere + " in other world(s) (ignored here: '" + here + "')";
            var sb = new System.Text.StringBuilder();
            sb.Append(mine.Count).Append(" here: ");
            for (int i = 0; i < mine.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('#').Append(i + 1).Append(' ').Append(mine[i].Describe());
            }
            if (elsewhere > 0) sb.Append(" (+").Append(elsewhere).Append(" in other world(s))");
            return sb.ToString();
        }
    }
}
