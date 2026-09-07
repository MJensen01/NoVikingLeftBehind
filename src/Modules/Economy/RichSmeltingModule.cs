using System;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Trailing-tier metal comes out richer: smelters, blast furnaces and kilns whose OUTPUT is a
    /// material behind the frontier spawn OutputMultiplier items per input, and crafting recipes
    /// whose output is such a material yield RecipeYieldMultiplier times as much.
    ///
    /// Patch points (0.221.12):
    ///
    ///   Smelter.Spawn(string ore, int stack) - the single funnel for everything a smelter emits.
    ///     Called from SpawnProcessed(), which only runs on the object's owner, so exactly one
    ///     client multiplies and the extra items go through the normal Instantiate + OnCreateNew
    ///     path. Multiplying `stack` in a prefix is one line and needs no knowledge of the queue.
    ///     Charcoal kilns and blast furnaces are Smelter components too, so they are covered by
    ///     the same patch; whether they DO anything is decided by the tier of m_conversion.m_to,
    ///     so wood -> coal (tier 0) is never touched.
    ///
    ///   Recipe.GetAmount(int, out int, out ItemDrop.ItemData, int) - the amount a craft produces.
    ///     Both InventoryGui.DoCrafting (what you actually get) and the craft-button label read it,
    ///     so display and result cannot disagree. This is deliberately a RUNTIME patch rather than
    ///     an ObjectDB rewrite: nothing is mutated, so the frontier moving or the config changing
    ///     takes effect on the next craft with no bookkeeping and no risk of a stale m_amount
    ///     being written into someone's save-adjacent state.
    ///
    /// The yield rule keys off the tier of the OUTPUT item, and the tier map only contains raw
    /// materials - so "Bronze" (tier 1) doubles, but "AxeBronze" is tier 0 and never does.
    /// </summary>
    internal sealed class RichSmeltingModule : FeatureModule
    {
        public override string Name => "RichSmelting";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Smelting";

        private static ConfigEntry<int> _outputMultiplier;
        private static ConfigEntry<int> _recipeYieldMultiplier;
        private static RichSmeltingModule _self;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        protected override void Bind()
        {
            _self = this;

            _outputMultiplier = BindSynced("OutputMultiplier", 2,
                "Smelters, blast furnaces and kilns produce this many items per input when the " +
                "OUTPUT material is behind the frontier (bronze/iron once the group has moved on). " +
                "1 = vanilla. Clamped to at least 1 and to the item's max stack size.");

            _recipeYieldMultiplier = BindSynced("RecipeYieldMultiplier", 2,
                "Crafting recipes whose OUTPUT is a material behind the frontier (Bronze at the " +
                "forge, BronzeNails, ...) yield this many times as much. Only raw materials listed " +
                "in [Tiers] MaterialTiers qualify, so tools and armour are never affected. " +
                "1 = vanilla.");
        }

        protected override void ApplyPatches()
        {
            var spawn = AccessTools.Method(typeof(Smelter), "Spawn", new[] { typeof(string), typeof(int) });
            if (spawn == null) throw new Exception("Smelter.Spawn(string,int) not found");
            Harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(RichSmeltingModule), nameof(SpawnPre)));

            var amount = AccessTools.Method(typeof(Recipe), "GetAmount", new[]
            {
                typeof(int), typeof(int).MakeByRefType(),
                typeof(ItemDrop.ItemData).MakeByRefType(), typeof(int)
            });
            if (amount == null)
                throw new Exception("Recipe.GetAmount(int,out int,out ItemDrop.ItemData,int) not found");
            Harmony.Patch(amount, postfix: new HarmonyMethod(typeof(RichSmeltingModule), nameof(RecipeAmountPost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        // ---- smelter output ---------------------------------------------------------------------

        private static void SpawnPre(Smelter __instance, string ore, ref int stack)
        {
            if (!Live() || stack <= 0) return;
            int mult = Mathf.Max(1, _outputMultiplier.Value);
            if (mult == 1) return;

            var to = OutputOf(__instance, ore);
            if (to == null) return;
            if (!Tiers.IsBehind(Tiers.OfItem(to.name))) return;

            long scaled = (long)stack * mult;
            int max = to.m_itemData != null && to.m_itemData.m_shared != null
                          ? to.m_itemData.m_shared.m_maxStackSize : 0;
            if (max > 0 && scaled > max) scaled = max;   // Spawn writes one ItemDrop with m_stack
            if (scaled < stack) scaled = stack;
            stack = (int)scaled;
        }

        /// <summary>Smelter.GetItemConversion is private; the list itself is public.</summary>
        private static ItemDrop OutputOf(Smelter smelter, string ore)
        {
            if (smelter == null || smelter.m_conversion == null || string.IsNullOrEmpty(ore)) return null;
            foreach (var c in smelter.m_conversion)
            {
                if (c == null || c.m_from == null || c.m_to == null) continue;
                if (c.m_from.gameObject.name == ore) return c.m_to;
            }
            return null;
        }

        // ---- crafting yield ----------------------------------------------------------------------

        private static void RecipeAmountPost(Recipe __instance, ref int __result)
        {
            if (!Live() || __result <= 0) return;
            int mult = Mathf.Max(1, _recipeYieldMultiplier.Value);
            if (mult == 1) return;
            if (__instance == null || __instance.m_item == null) return;
            if (!Tiers.IsBehind(Tiers.OfItem(__instance.m_item.name))) return;

            long scaled = (long)__result * mult;
            __result = scaled > int.MaxValue ? int.MaxValue : (int)scaled;
        }

        // ---- reporting ----------------------------------------------------------------------------

        private string Numbers()
        {
            return "smelter output x" + Mathf.Max(1, _outputMultiplier.Value) +
                   ", recipe yield x" + Mathf.Max(1, _recipeYieldMultiplier.Value) +
                   " for outputs in behind-the-frontier tiers: " + Frontier.BehindRangeText();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override string StatusDetail()
        {
            return Numbers();
        }

        /// <summary>Headless proof: which conversions and which recipes would be multiplied now.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][RichSmelting] OutputMultiplier=")
              .Append(_outputMultiplier != null ? _outputMultiplier.Value : -1)
              .Append(" RecipeYieldMultiplier=")
              .Append(_recipeYieldMultiplier != null ? _recipeYieldMultiplier.Value : -1)
              .Append(" behind=").Append(Frontier.BehindRangeText());

            int convTotal = 0, convHit = 0;
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null)
            {
                sb.Append("\n  ZNetScene has no prefabs - cannot list conversions");
            }
            else
            {
                foreach (var go in scene.m_prefabs)
                {
                    if (go == null) continue;
                    var sm = go.GetComponent<Smelter>();
                    if (sm == null || sm.m_conversion == null) continue;
                    foreach (var c in sm.m_conversion)
                    {
                        if (c == null || c.m_from == null || c.m_to == null) continue;
                        convTotal++;
                        int tier = Tiers.OfItem(c.m_to.name);
                        if (!Tiers.IsBehind(tier)) continue;
                        convHit++;
                        sb.Append("\n  smelt ").Append(go.name).Append(": ")
                          .Append(Tiers.CleanName(c.m_from.gameObject.name)).Append(" -> ")
                          .Append(Tiers.CleanName(c.m_to.name)).Append("(t").Append(tier).Append(") x")
                          .Append(Mathf.Max(1, _outputMultiplier != null ? _outputMultiplier.Value : 1));
                    }
                }
                sb.Append("\n  conversions: ").Append(convHit).Append(" of ").Append(convTotal)
                  .Append(" would be multiplied");
            }

            var odb = ObjectDB.instance;
            if (odb == null || odb.m_recipes == null)
            {
                sb.Append("\n  ObjectDB has no recipes - cannot list yields");
                return sb.ToString();
            }

            int recHit = 0;
            foreach (var r in odb.m_recipes)
            {
                if (r == null || r.m_item == null) continue;
                int tier = Tiers.OfItem(r.m_item.name);
                if (!Tiers.IsBehind(tier)) continue;
                recHit++;
                if (recHit <= 12)
                    sb.Append("\n  craft ").Append(Tiers.CleanName(r.m_item.name))
                      .Append("(t").Append(tier).Append(") ").Append(r.m_amount).Append("->")
                      .Append(r.m_amount * Mathf.Max(1, _recipeYieldMultiplier != null ? _recipeYieldMultiplier.Value : 1));
            }
            sb.Append("\n  recipes: ").Append(recHit).Append(" of ").Append(odb.m_recipes.Count)
              .Append(" would yield more");
            return sb.ToString();
        }
    }
}
