using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The Builder skill's storage and maths - stage 2 of docs/BUILD-COSTS-PLAN.md.
    ///
    /// An NVLB-TRACKED skill, not a vanilla one. Vanilla's <c>Skills</c> is a fixed enum baked into
    /// the save format and into the skills panel; adding to it needs a third-party SkillManager,
    /// brings the death penalty (a builder losing build progress for dying to a greydwarf is not
    /// the feature Matt asked for) and would have to be installed by every client to read a
    /// character file. So the skill lives where the mod's other per-character records already live:
    /// <c>Player.m_customData["nvlb.builder"]</c>, the Dictionary&lt;string,string&gt; vanilla
    /// persists verbatim in <c>Player.Save</c> (save version &gt;= 26) - the same channel as the
    /// grave record, the loadouts and the power slots. It therefore travels with the CHARACTER,
    /// survives a relog and a world change, and is invisible to a vanilla client rather than
    /// corrupting anything.
    ///
    /// THE CURVE IS VANILLA'S, not an approximation of it: the XP needed for the next level is
    /// <c>pow(floor(level + 1), 1.5) * 0.5 + 0.5</c> and the accumulator resets to 0 on level-up,
    /// copied from <c>Skills.Skill.GetNextLevelRequirement()</c> / <c>Raise()</c> in the 1.0.7
    /// decompile, so "Builder Lv 40" means what a Valheim player already thinks it means. The one
    /// difference is deliberate: <see cref="Raise"/> loops, because one big piece can hand over
    /// more XP than the first few levels need, and vanilla's single-level-per-call rule would
    /// silently bin the surplus.
    ///
    /// FORMAT (invariant culture): <c>1|&lt;level&gt;|&lt;accumulator&gt;</c>. Versioned like the
    /// grave record so a later shape can be added without a migration; anything unparseable
    /// decodes to a fresh level-0 record rather than throwing, because a corrupt string must never
    /// be able to stop a player building.
    /// </summary>
    internal static class BuilderSkill
    {
        public const string Key = "nvlb.builder";
        private const int Version = 1;

        public const float MaxLevel = 100f;

        /// <summary>One character's Builder progress.</summary>
        internal struct Record
        {
            public float Level;
            public float Accumulator;

            public Record(float level, float accumulator)
            {
                Level = Mathf.Clamp(level, 0f, MaxLevel);
                Accumulator = accumulator < 0f || float.IsNaN(accumulator) ? 0f : accumulator;
            }
        }

        // ---- pure maths (this is what the self test exercises) ----------------------------------

        /// <summary>Vanilla's own curve: Skills.Skill.GetNextLevelRequirement().</summary>
        internal static float NextLevelRequirement(float level)
        {
            return Mathf.Pow(Mathf.Floor(Mathf.Clamp(level, 0f, MaxLevel) + 1f), 1.5f) * 0.5f + 0.5f;
        }

        /// <summary>Add XP. Loops, unlike vanilla's single-level Raise - see the class doc.</summary>
        internal static Record Raise(Record r, float xp, out int levelsGained)
        {
            levelsGained = 0;
            if (xp <= 0f || float.IsNaN(xp)) return r;
            if (r.Level >= MaxLevel) return new Record(MaxLevel, 0f);

            r.Accumulator += xp;
            // Guard the loop as well as the level: a nonsense config could otherwise spin here.
            while (r.Level < MaxLevel && r.Accumulator >= NextLevelRequirement(r.Level) && levelsGained < 200)
            {
                r.Accumulator = 0f;      // vanilla drops the surplus at each level-up
                r.Level += 1f;
                levelsGained++;
            }
            if (r.Level >= MaxLevel) { r.Level = MaxLevel; r.Accumulator = 0f; }
            return r;
        }

        /// <summary>
        /// The cost multiplier this level gives: linear from 1.0 at level 0 to
        /// <c>1 - maxDiscount</c> at level 100. Lv 50 with the default 0.30 = x0.85.
        /// </summary>
        internal static float FactorFor(float level, float maxDiscount)
        {
            if (float.IsNaN(maxDiscount) || maxDiscount <= 0f) return 1f;
            maxDiscount = Mathf.Clamp(maxDiscount, 0f, 0.9f);
            float f = 1f - maxDiscount * (Mathf.Clamp(level, 0f, MaxLevel) / MaxLevel);
            return Mathf.Clamp(f, 0.01f, 1f);
        }

        internal static string Encode(Record r)
        {
            var c = CultureInfo.InvariantCulture;
            return Version.ToString(c) + "|" + r.Level.ToString("0.####", c) + "|" +
                   r.Accumulator.ToString("0.####", c);
        }

        /// <summary>Never throws: anything unparseable is a fresh level-0 record.</summary>
        internal static Record Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return new Record(0f, 0f);
            var c = CultureInfo.InvariantCulture;
            var parts = s.Split('|');
            int ver;
            if (parts.Length < 3 ||
                !int.TryParse(parts[0], NumberStyles.Integer, c, out ver) || ver != Version)
                return new Record(0f, 0f);

            float level, acc;
            if (!float.TryParse(parts[1], NumberStyles.Float, c, out level)) level = 0f;
            if (!float.TryParse(parts[2], NumberStyles.Float, c, out acc)) acc = 0f;
            if (float.IsNaN(level) || float.IsInfinity(level)) level = 0f;
            if (float.IsNaN(acc) || float.IsInfinity(acc)) acc = 0f;
            return new Record(level, acc);
        }

        // ---- player binding ----------------------------------------------------------------------

        // The build HUD asks for the factor several times a frame, so the parse is cached against
        // the player it came from AND the exact string it was parsed from - which means an edit
        // from anywhere else (a console command, another module) is picked up on the next read
        // without any invalidation protocol.
        private static Player _cachedPlayer;
        private static string _cachedRaw;
        private static Record _cached;

        private static Dictionary<string, string> DataOf(Player p)
        {
            if (p == null) return null;
            if (p.m_customData == null) p.m_customData = new Dictionary<string, string>();
            return p.m_customData;
        }

        internal static string Raw(Player p)
        {
            var data = DataOf(p);
            string raw;
            return data != null && data.TryGetValue(Key, out raw) ? raw : null;
        }

        internal static Record Get(Player p)
        {
            if (p == null) return new Record(0f, 0f);
            var raw = Raw(p) ?? "";
            if (ReferenceEquals(p, _cachedPlayer) && string.Equals(raw, _cachedRaw, StringComparison.Ordinal))
                return _cached;

            _cachedPlayer = p;
            _cachedRaw = raw;
            _cached = Decode(raw);
            return _cached;
        }

        internal static void Set(Player p, Record r)
        {
            var data = DataOf(p);
            if (data == null) return;
            var raw = Encode(r);
            data[Key] = raw;
            _cachedPlayer = p;
            _cachedRaw = raw;
            _cached = r;
        }

        /// <summary>"Lv 31 (4.2/17.9 xp)" - for nvlb.status, the console command and the log.</summary>
        internal static string Describe(Record r)
        {
            var c = CultureInfo.InvariantCulture;
            if (r.Level >= MaxLevel) return "Lv 100 (max)";
            return "Lv " + Mathf.FloorToInt(r.Level).ToString(c) +
                   " (" + r.Accumulator.ToString("0.#", c) + "/" +
                   NextLevelRequirement(r.Level).ToString("0.#", c) + " xp)";
        }
    }
}
