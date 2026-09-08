using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The world's progress, expressed as a tier 0-7 read from the boss global keys.
    ///
    /// Key names verified against the 0.221.12 decompile: ZoneSystem.GlobalKeys is an enum
    /// containing defeated_eikthyr / defeated_gdking / defeated_bonemass / defeated_dragon /
    /// defeated_goblinking. The Queen and Fader keys are not enum members - they are set from
    /// the boss prefab's m_defeatSetGlobalKey - so every lookup here goes through
    /// ZoneSystem.GetGlobalKey(string), which does m_globalKeysValues.TryGetValue(name.ToLower()).
    /// That works for both kinds on both ends (clients get the full key set via RPC_GlobalKeys).
    /// </summary>
    internal static class Frontier
    {
        public const string Section = "Frontier";

        /// <summary>Index i => tier i+1.</summary>
        public static readonly string[] BossKeys =
        {
            "defeated_eikthyr",     // 1 Eikthyr
            "defeated_gdking",      // 2 The Elder
            "defeated_bonemass",    // 3 Bonemass
            "defeated_dragon",      // 4 Moder
            "defeated_goblinking",  // 5 Yagluth
            "defeated_queen",       // 6 The Queen
            "defeated_fader"        // 7 Fader
        };

        public const int MaxTier = 7;

        public static ConfigEntry<int> TierOverride;
        public static ConfigEntry<int> TiersBehind;

        private static int _autoTier;
        private static bool _known;

        /// <summary>Highest boss tier the world has cleared, or the configured override.</summary>
        public static int WorldTier
        {
            get
            {
                if (IsOverridden) return Math.Min(MaxTier, TierOverride.Value);
                return _autoTier;
            }
        }

        public static bool IsOverridden => TierOverride != null && TierOverride.Value >= 0;

        /// <summary>The tier read from the world's keys, ignoring any override.</summary>
        public static int AutoTier => _autoTier;

        public static void BindConfig()
        {
            TierOverride = NoVikingLeftBehindPlugin.BindSynced(Section, "TierOverride", -1,
                "Force the world tier instead of reading the boss keys. -1 = auto. " +
                "0 = no boss killed, 1 Eikthyr, 2 The Elder, 3 Bonemass, 4 Moder, 5 Yagluth, " +
                "6 The Queen, 7 Fader. For testing.", null);

            TiersBehind = NoVikingLeftBehindPlugin.BindSynced(Section, "TiersBehind", 1,
                "How many tiers below the world tier a material has to be before the catch-up " +
                "rules apply to it. 1 = everything up to WorldTier-1 is 'behind the frontier' " +
                "(the group killed the Elder -> WorldTier 2 -> bronze, tier 1, qualifies).", null);

            NoVikingLeftBehindPlugin.ConfigChanged += OnConfigChanged;
        }

        private static void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == TierOverride || entry == TiersBehind)
                NoVikingLeftBehindPlugin.Log.LogInfo("[Frontier] config changed: WorldTier=" + Describe() +
                                           " TiersBehind=" + TiersBehind.Value);
        }

        /// <summary>Re-read the boss keys. Returns true if the auto tier changed.</summary>
        public static bool Recompute()
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return false;

            int tier = 0;
            for (int i = 0; i < BossKeys.Length; i++)
                if (zs.GetGlobalKey(BossKeys[i])) tier = i + 1;

            bool changed = !_known || tier != _autoTier;
            int old = _autoTier;
            _autoTier = tier;
            _known = true;

            if (changed)
                NoVikingLeftBehindPlugin.Log.LogInfo("[Frontier] WorldTier=" + Describe() +
                                           (IsOverridden ? " [auto would be " + tier + "]"
                                                         : " (was " + old + ", key " + KeyFor(tier) + ")") +
                                           " TiersBehind=" + (TiersBehind != null ? TiersBehind.Value : 1));
            return changed;
        }

        public static string KeyFor(int tier)
        {
            if (tier <= 0) return "none";
            if (tier > BossKeys.Length) return "?";
            return BossKeys[tier - 1];
        }

        public static string Describe()
        {
            return IsOverridden ? WorldTier + " (override)" : WorldTier + " (auto)";
        }

        public static string BehindRangeText()
        {
            int max = WorldTier - (TiersBehind != null ? TiersBehind.Value : 1);
            return max < 1 ? "none" : "1.." + max;
        }
    }

    /// <summary>
    /// Keeps <see cref="Frontier"/> current on both ends and logs every change.
    ///
    /// Hook: postfix on the private ZoneSystem.GlobalKeyAdd(string, bool). That is the single
    /// funnel every key goes through on both halves - the server's world load (ZoneSystem.Load),
    /// RPC_SetGlobalKey, and the client's RPC_GlobalKeys full-set replay all call it.
    /// GlobalKeyRemove and ClearGlobalKeys are covered too so a removal cannot leave a stale tier.
    /// </summary>
    internal sealed class FrontierModule : FeatureModule
    {
        public override string Name => "Frontier";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => Frontier.Section;

        protected override void Bind()
        {
            // Frontier's own entries are bound by the plugin before module discovery, because
            // other modules read them while binding. Nothing to add here.
        }

        protected override void ApplyPatches()
        {
            var add = AccessTools.Method(typeof(ZoneSystem), "GlobalKeyAdd",
                                         new[] { typeof(string), typeof(bool) });
            if (add == null)
                throw new Exception("ZoneSystem.GlobalKeyAdd(string,bool) not found");
            Harmony.Patch(add, postfix: new HarmonyMethod(typeof(FrontierModule), nameof(KeysChanged)));

            var remove = AccessTools.Method(typeof(ZoneSystem), "GlobalKeyRemove",
                                            new[] { typeof(string), typeof(bool) });
            if (remove != null)
                Harmony.Patch(remove, postfix: new HarmonyMethod(typeof(FrontierModule), nameof(KeysChanged)));

            var clear = AccessTools.Method(typeof(ZoneSystem), "ClearGlobalKeys");
            if (clear != null)
                Harmony.Patch(clear, postfix: new HarmonyMethod(typeof(FrontierModule), nameof(KeysChanged)));

            var zsStart = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (zsStart != null)
                Harmony.Patch(zsStart, postfix: new HarmonyMethod(typeof(FrontierModule), nameof(KeysChanged)));
        }

        private static void KeysChanged()
        {
            try
            {
                Frontier.Recompute();
                Tiers.ValidateOnce();
                // Same "world is ready" point, same reason: by ZoneSystem.Start ObjectDB's recipe
                // list is populated on BOTH halves, so the station-multiplier proof line can name a
                // real match count. TrailingTierDiscount is client-side and never patches on a
                // dedicated server, so it cannot hook this itself.
                TrailingTierDiscountModule.ReportStationsOnce();
            }
            catch (Exception e)
            {
                Log.LogError("[Frontier] recompute failed: " + e);
            }
        }

        public override string StatusDetail()
        {
            return "WorldTier=" + Frontier.Describe() + " autoKey=" + Frontier.KeyFor(Frontier.AutoTier) +
                   " TiersBehind=" + Frontier.TiersBehind.Value +
                   " materials=" + Tiers.Count + " unknown=" + Tiers.UnknownCount;
        }
    }
}
