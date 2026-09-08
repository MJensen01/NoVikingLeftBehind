using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Haldor also sells bars of trailing-tier metals, so a latecomer can buy the bronze the group
    /// already mined out instead of asking for a hand-out.
    ///
    /// Patch point: postfix on Trader.Start(). m_items is a plain serialized List on the Trader
    /// component, deep-copied per instance by Unity's Instantiate, and Trader.GetAvailableItems()
    /// re-filters it on every interaction - so appending at Start is enough and needs no further
    /// hook. Entries are deduped by prefab name against whatever is already in the list, so a
    /// second Start (re-entering the zone, another mod re-running it) cannot stack duplicates.
    ///
    /// The gate is vanilla's own: TradeItem.m_requiredGlobalKey is set to the boss key that would
    /// put the material behind the frontier, i.e. KeyFor(materialTier + TiersBehind). Bronze is
    /// tier 1 and TiersBehind is 1, so it needs defeated_gdking - kill the Elder and bronze appears
    /// in Haldor's list on its own, with no further work from this module.
    ///
    /// Side is Client because the trader is a client-side object: the store window is built
    /// locally from the local Trader component. The server half of the DLL only carries the config.
    /// </summary>
    internal sealed class TraderStockModule : FeatureModule
    {
        public override string Name => "TraderStock";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Trader";

        public override string Theme => "Catching up";

        public override string Hint => "Traders sell trailing-tier metal bars once you have caught up";

        internal const string DefaultItems = "Bronze:5:60,Iron:5:80,Silver:5:120,BlackMetal:5:150";

        private static ConfigEntry<string> _items;
        private static ConfigEntry<string> _traderNames;
        private static TraderStockModule _self;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        private sealed class Entry
        {
            public string Prefab;
            public int Stack;
            public int Price;
        }

        protected override void Bind()
        {
            _self = this;

            _items = BindSynced("Items", DefaultItems,
                "Comma-separated PrefabName:stack:price entries added to the trader's stock. An " +
                "entry only appears once its material is behind the frontier: it is gated by " +
                "vanilla's own TradeItem.m_requiredGlobalKey, set to the boss key for " +
                "(material tier + [Frontier] TiersBehind). Unknown prefab names are logged and " +
                "skipped. Materials at tier 0, or whose gating boss is past the last tier, are " +
                "skipped too.",
                Opt.T("Which trailing-tier items a trader stocks, and at what price"));

            _traderNames = BindSynced("TraderNames", "Haldor",
                "Comma-separated trader prefab names (or Trader.m_name values) that get the extra " +
                "stock. Default: Haldor only. Add Hildir or BogWitch to include them. '*' means " +
                "every trader.",
                Opt.T("Which traders get the extra trailing-tier stock"));
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(Trader), "Start");
            if (start == null) throw new Exception("Trader.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(TraderStockModule), nameof(StartPost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private static void StartPost(Trader __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                Stock(__instance);
            }
            catch (Exception e)
            {
                Log.LogWarning("[TraderStock] could not add stock to " + __instance.name + ": " + e.Message);
            }
        }

        private static void Stock(Trader trader)
        {
            if (!Matches(trader)) return;
            if (trader.m_items == null) trader.m_items = new List<Trader.TradeItem>();

            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in trader.m_items)
                if (it != null && it.m_prefab != null) have.Add(Tiers.CleanName(it.m_prefab.name));

            int added = 0;
            foreach (var e in Parse(_items != null ? _items.Value : DefaultItems, true))
            {
                if (have.Contains(e.Prefab)) continue;

                string key;
                var drop = Resolve(e.Prefab, out key, true);
                if (drop == null) continue;

                trader.m_items.Add(new Trader.TradeItem
                {
                    m_prefab = drop,
                    m_stack = Mathf.Max(1, e.Stack),
                    m_price = Mathf.Max(1, e.Price),
                    m_requiredGlobalKey = key
                });
                have.Add(e.Prefab);
                added++;
            }

            if (added > 0)
                Log.LogInfo("[TraderStock] " + trader.name + " (" + trader.m_name + "): added " +
                            added + " item(s), total " + trader.m_items.Count);
        }

        private static bool Matches(Trader trader)
        {
            var names = _traderNames != null ? _traderNames.Value : "Haldor";
            foreach (var raw in names.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = raw.Trim();
                if (n.Length == 0) continue;
                if (n == "*") return true;
                if (Tiers.CleanName(trader.name).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (!string.IsNullOrEmpty(trader.m_name) &&
                    trader.m_name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ---- config parsing ----------------------------------------------------------------------

        private static List<Entry> Parse(string raw, bool warn)
        {
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (var chunk in raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = chunk.Trim();
                if (piece.Length == 0) continue;

                var bits = piece.Split(':');
                int stack, price;
                if (bits.Length != 3 ||
                    !int.TryParse(bits[1].Trim(), out stack) ||
                    !int.TryParse(bits[2].Trim(), out price))
                {
                    if (warn) Log.LogWarning("[TraderStock] ignoring '" + piece + "' (want Prefab:stack:price)");
                    continue;
                }
                list.Add(new Entry { Prefab = bits[0].Trim(), Stack = stack, Price = price });
            }
            return list;
        }

        /// <summary>Prefab -> ItemDrop plus the boss key that gates it. null = skip (and why).</summary>
        private static ItemDrop Resolve(string prefabName, out string requiredKey, bool warn)
        {
            requiredKey = null;

            int tier = Tiers.OfItem(prefabName);
            if (tier <= 0)
            {
                if (warn) Log.LogWarning("[TraderStock] '" + prefabName + "' is tier 0 in [Tiers] MaterialTiers - skipped");
                return null;
            }

            int gateTier = tier + (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1);
            if (gateTier > Frontier.MaxTier)
            {
                if (warn) Log.LogWarning("[TraderStock] '" + prefabName + "' (tier " + tier + ") can never be " +
                                         gateTier + " tiers behind - skipped");
                return null;
            }

            // With a [Frontier] TierOverride in force the real boss key would not match the
            // simulated progress, so trust the override and leave the key empty.
            requiredKey = Frontier.IsOverridden ? "" : Frontier.KeyFor(gateTier);
            if (Frontier.IsOverridden && !Tiers.IsBehind(tier)) return null;

            var odb = ObjectDB.instance;
            var go = odb != null ? odb.GetItemPrefab(prefabName) : null;
            var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null && warn)
                Log.LogWarning("[TraderStock] '" + prefabName + "' not found in ObjectDB - skipped");
            return drop;
        }

        // ---- reporting ------------------------------------------------------------------------------

        private string Numbers()
        {
            var parsed = Parse(_items.Value, false);
            return parsed.Count + " item(s) for " + _traderNames.Value +
                   " [" + _items.Value + "], gated by boss key for (tier + " +
                   Frontier.TiersBehind.Value + ")";
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (!Active) return;
            Log.LogInfo("[" + Name + "] " + Numbers() +
                        " - traders already in the world keep their current list until they respawn");
        }

        public override string StatusDetail()
        {
            return Numbers();
        }

        /// <summary>Headless proof: what would be added to Haldor, and under which key.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][TraderStock] TraderNames=")
              .Append(_traderNames != null ? _traderNames.Value : "?")
              .Append(" Items=").Append(_items != null ? _items.Value : "?")
              .Append(" ").Append(Frontier.Describe())
              .Append(" TiersBehind=").Append(Frontier.TiersBehind.Value);

            foreach (var e in Parse(_items != null ? _items.Value : DefaultItems, false))
            {
                int tier = Tiers.OfItem(e.Prefab);
                string key;
                var drop = Resolve(e.Prefab, out key, false);
                sb.Append("\n  ").Append(e.Prefab).Append(" t").Append(tier)
                  .Append(" stack=").Append(e.Stack).Append(" price=").Append(e.Price)
                  .Append(" key=").Append(string.IsNullOrEmpty(key) ? "(none)" : key)
                  .Append(drop == null ? " -> SKIPPED" : " -> would be added")
                  .Append(Tiers.IsBehind(tier) ? " [behind the frontier now]" : " [not behind yet]");
            }
            return sb.ToString();
        }
    }
}
