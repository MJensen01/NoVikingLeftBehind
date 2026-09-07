using System;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Eaten food keeps its full health/stamina/eitr contribution until it expires instead of
    /// fading toward the end of its timer.
    ///
    /// Patch point (0.221.12): postfix on the private <c>Player.UpdateFood(float dt, bool
    /// forceUpdate)</c>. Vanilla's batch (once <c>m_foodUpdateTimer &gt;= 1f</c>) does, for every
    /// entry in <c>m_foods</c>:
    ///   <c>food.m_time -= 1f; f = pow(clamp01(m_time / burnTime), 0.3f);
    ///      m_health = shared.m_food * f; m_stamina = shared.m_foodStamina * f;
    ///      m_eitr = shared.m_foodEitr * f;</c>
    /// then sums all three across <c>m_foods</c> (plus <c>m_baseHP</c>/<c>m_baseStamina</c>) and
    /// calls <c>SetMaxHealth</c>/<c>SetMaxStamina</c>/<c>SetMaxEitr</c>. Expired foods
    /// (<c>m_time &lt;= 0</c>) are removed from <c>m_foods</c> by that same vanilla loop before our
    /// postfix runs, so expiry needs no special handling here - we simply never see them again.
    ///
    /// This module re-derives each food's fraction with a (normally identical) exponent, floors it
    /// at <c>KeepFraction</c>, and raises <c>m_health</c>/<c>m_stamina</c>/<c>m_eitr</c> up to that
    /// floor - i.e. <c>max(vanilla, shared * KeepFraction)</c>, exactly as specced. It never lowers
    /// a value below what vanilla computed. Totals are then re-summed and re-pushed through
    /// <c>SetMaxHealth</c>/<c>SetMaxStamina</c>/<c>SetMaxEitr</c> so max HP tracks the boosted
    /// values (<c>GetTotalFoodValue</c> just sums the same fields).
    ///
    /// <c>GetBaseFoodHP()</c> and <c>m_baseHP</c>/<c>m_baseStamina</c> are the same field
    /// (<c>Player.GetBaseFoodHP()</c> is a public getter for the private <c>m_baseHP</c>;
    /// <c>m_baseStamina</c> is a public field). <c>SetMaxEitr</c> is private, so it is invoked via a
    /// cached <c>MethodInfo</c> rather than patched directly (no reason to control-flow it).
    ///
    /// Hud.UpdateFood(Player) (decompiled) confirms the food-bar icons only ever render
    /// <c>food.m_time</c> (remaining seconds/minutes text) and <c>food.CanEatAgain()</c> (itself
    /// only a function of <c>m_time</c> vs. half of <c>m_foodBurnTime</c>) - never
    /// <c>m_health</c>/<c>m_stamina</c>/<c>m_eitr</c>. The bar's overall length comes from
    /// <c>player.GetMaxHealth()</c>, which *does* track our boosted totals via SetMaxHealth. So：
    /// each food icon keeps counting down real time exactly as vanilla (no UI lie about "time
    /// left") while the health/stamina/eitr bars correctly reflect the undecayed values.
    /// </summary>
    internal sealed class FoodNoDecayModule : FeatureModule
    {
        public override string Name => "FoodNoDecay";

        /// <summary>
        /// Normally Client (the shipping default). Setting the machine-local [Food] SelfTest = true
        /// flips this to Both so the headless test server (0 players) can prove ObjectDB lookup and
        /// the recompute maths on its own - the postfix itself still gates on ClientActive() and
        /// stays inert there. Safe to key off the config value: Plugin.Awake calls Configure()
        /// (which runs Bind()) before TryEnable() reads Side. Same trick as VanguardShadow (§13.3).
        /// </summary>
        public override ModuleSide Side => _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        public override string Section => "Food";

        private static ConfigEntry<float> _keepFraction;
        private static ConfigEntry<float> _curveExponent;
        private static ConfigEntry<bool> _selfTest;
        private static FoodNoDecayModule _self;
        private static MethodInfo _setMaxEitr;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        protected override void Bind()
        {
            _self = this;

            _keepFraction = BindSynced("KeepFraction", 1f,
                "Floor applied to each eaten food's health/stamina/eitr contribution, as a " +
                "fraction of its full (freshly-eaten) value: 1.0 = no decay at all until the food " +
                "expires (default). 0.5 = the value never decays below half, but may still decay " +
                "further towards 0.5 like vanilla. 0.0 = vanilla behaviour, unchanged.");

            _curveExponent = BindSynced("CurveExponent", 0.3f,
                "Exponent used to re-derive the vanilla decay fraction from remaining-time / " +
                "burn-time before the KeepFraction floor is applied. Leave at 0.3 (vanilla's own " +
                "curve) unless you specifically want a different decay shape for the portion below " +
                "KeepFraction.");

            _selfTest = BindLocal("SelfTest", false,
                "Local debug only, not synced. When true, on (re)load and on Enabled toggling logs " +
                "a comparison of vanilla-decayed vs. kept food values for CookedMeat at 10% of its " +
                "burn time, via ObjectDB. Leave false in normal play.");
        }

        protected override void ApplyPatches()
        {
            var update = AccessTools.Method(typeof(Player), "UpdateFood", new[] { typeof(float), typeof(bool) });
            if (update == null) throw new Exception("Player.UpdateFood(float,bool) not found");
            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(UpdateFoodPost)));

            _setMaxEitr = AccessTools.Method(typeof(Player), "SetMaxEitr", new[] { typeof(float), typeof(bool) });
            if (_setMaxEitr == null) throw new Exception("Player.SetMaxEitr(float,bool) not found");

            // ObjectDB.instance is still null at plugin Awake (this method runs from there), so
            // SelfTest cannot run yet even if enabled. Only when SelfTest is on do we additionally
            // hook ObjectDB.Awake/CopyOtherDB (postfix, same funnel VanguardShadow uses, §13.1) to
            // run it once ObjectDB actually has items - never installed in the shipping default.
            if (_selfTest.Value)
            {
                var awake = AccessTools.Method(typeof(ObjectDB), "Awake");
                if (awake == null) throw new Exception("ObjectDB.Awake() not found");
                var copy = AccessTools.Method(typeof(ObjectDB), "CopyOtherDB", new[] { typeof(ObjectDB) });
                if (copy == null) throw new Exception("ObjectDB.CopyOtherDB(ObjectDB) not found");
                Harmony.Patch(awake, postfix: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(ObjectDBPost)));
                Harmony.Patch(copy, postfix: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(ObjectDBPost)));
            }

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        /// <summary>Postfix for ObjectDB.Awake / ObjectDB.CopyOtherDB, only installed when SelfTest is on.</summary>
        private static void ObjectDBPost(ObjectDB __instance)
        {
            if (_self == null || !_self.Active || _selfTest == null || !_selfTest.Value) return;
            try { Log.LogInfo(SelfTest()); }
            catch (Exception e) { Log.LogWarning("[FoodNoDecay] SelfTest threw: " + e); }
        }

        // ---- the recompute ------------------------------------------------------------------------

        /// <summary>
        /// Re-derives one food's fraction-of-full value and floors it at KeepFraction. Pure
        /// function of config + the food's own fields, shared by the live postfix and SelfTest so
        /// they can never disagree.
        /// </summary>
        private static void RecomputeFood(Player.Food food, out float health, out float stamina, out float eitr)
        {
            var shared = food.m_item.m_shared;
            float burn = shared.m_foodBurnTime;
            float frac = burn > 0f ? Mathf.Clamp01(food.m_time / burn) : 0f;
            float curved = Mathf.Pow(frac, _curveExponent != null ? _curveExponent.Value : 0.3f);
            float floor = Mathf.Clamp01(_keepFraction != null ? _keepFraction.Value : 1f);
            float eff = Mathf.Max(curved, floor);
            health = shared.m_food * eff;
            stamina = shared.m_foodStamina * eff;
            eitr = shared.m_foodEitr * eff;
        }

        private static void UpdateFoodPost(Player __instance)
        {
            if (!Live() || __instance != Player.m_localPlayer) return;

            var foods = __instance.GetFoods();
            if (foods == null || foods.Count == 0) return;

            bool changed = false;
            foreach (var food in foods)
            {
                if (food == null || food.m_item == null || food.m_item.m_shared == null) continue;
                if (food.m_time <= 0f) continue; // vanilla already removed it from m_foods otherwise

                RecomputeFood(food, out var h, out var s, out var e);
                if (h > food.m_health) { food.m_health = h; changed = true; }
                if (s > food.m_stamina) { food.m_stamina = s; changed = true; }
                if (e > food.m_eitr) { food.m_eitr = e; changed = true; }
            }
            if (!changed) return;

            float hp = __instance.GetBaseFoodHP();
            float stamina2 = __instance.m_baseStamina;
            float eitr2 = 0f;
            foreach (var food in foods)
            {
                hp += food.m_health;
                stamina2 += food.m_stamina;
                eitr2 += food.m_eitr;
            }
            __instance.SetMaxHealth(hp, flashBar: true);
            __instance.SetMaxStamina(stamina2, flashBar: true);
            if (_setMaxEitr != null) _setMaxEitr.Invoke(__instance, new object[] { eitr2, true });
        }

        // ---- reporting -----------------------------------------------------------------------------

        private string Numbers()
        {
            return "KeepFraction=" + (_keepFraction != null ? _keepFraction.Value.ToString("0.###") : "?") +
                   " CurveExponent=" + (_curveExponent != null ? _curveExponent.Value.ToString("0.###") : "?");
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
            if (Active && _selfTest != null && _selfTest.Value && ObjectDB.instance != null)
            {
                try { Log.LogInfo(SelfTest()); }
                catch (Exception e) { Log.LogWarning("[FoodNoDecay] SelfTest threw: " + e); }
            }
        }

        public override string StatusDetail()
        {
            return Numbers();
        }

        /// <summary>
        /// Headless proof: builds a fake Food from ObjectDB's CookedMeat prefab with m_time at 10%
        /// of its burn time, then logs what vanilla would have set vs. what RecomputeFood keeps.
        /// </summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][FoodNoDecay] KeepFraction=")
              .Append(_keepFraction != null ? _keepFraction.Value : -1f)
              .Append(" CurveExponent=").Append(_curveExponent != null ? _curveExponent.Value : -1f);

            var odb = ObjectDB.instance;
            if (odb == null) { sb.Append("\n  ObjectDB not ready"); return sb.ToString(); }

            var prefab = odb.GetItemPrefab("CookedMeat");
            if (prefab == null) { sb.Append("\n  CookedMeat prefab not found in ObjectDB"); return sb.ToString(); }

            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null || itemDrop.m_itemData == null || itemDrop.m_itemData.m_shared == null)
            {
                sb.Append("\n  CookedMeat has no ItemDrop/ItemData");
                return sb.ToString();
            }

            var shared = itemDrop.m_itemData.m_shared;
            if (shared.m_foodBurnTime <= 0f)
            {
                sb.Append("\n  CookedMeat m_foodBurnTime <= 0, cannot test");
                return sb.ToString();
            }

            var food = new Player.Food { m_item = itemDrop.m_itemData, m_time = shared.m_foodBurnTime * 0.1f };

            float vanillaFrac = Mathf.Clamp01(food.m_time / shared.m_foodBurnTime);
            float vanillaCurved = Mathf.Pow(vanillaFrac, 0.3f); // vanilla's own hardcoded exponent
            float vanillaHealth = shared.m_food * vanillaCurved;
            float vanillaStamina = shared.m_foodStamina * vanillaCurved;
            float vanillaEitr = shared.m_foodEitr * vanillaCurved;

            RecomputeFood(food, out var keptHealth, out var keptStamina, out var keptEitr);
            float finalHealth = Mathf.Max(vanillaHealth, keptHealth);
            float finalStamina = Mathf.Max(vanillaStamina, keptStamina);
            float finalEitr = Mathf.Max(vanillaEitr, keptEitr);

            sb.Append("\n  CookedMeat burnTime=").Append(shared.m_foodBurnTime)
              .Append(" atTime=").Append(food.m_time).Append(" (10% remaining)");
            sb.Append("\n  vanilla-decayed: health=").Append(vanillaHealth)
              .Append(" stamina=").Append(vanillaStamina).Append(" eitr=").Append(vanillaEitr);
            sb.Append("\n  kept (post-recompute): health=").Append(finalHealth)
              .Append(" stamina=").Append(finalStamina).Append(" eitr=").Append(finalEitr);
            return sb.ToString();
        }
    }
}
