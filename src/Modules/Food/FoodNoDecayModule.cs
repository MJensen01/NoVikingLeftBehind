using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
    /// HOW (0.4.2 - this changed): a TRANSPILER on the private <c>Player.UpdateFood(float dt, bool
    /// forceUpdate)</c> that replaces vanilla's decay curve at the source. Vanilla's per-second
    /// batch is (0.221.12 decompile, Player.cs:2227):
    ///
    ///   <c>food.m_time -= 1f;
    ///      float f = Mathf.Clamp01(food.m_time / burnTime);
    ///      f = Mathf.Pow(f, 0.3f);                                  &lt;-- the one call we swap
    ///      food.m_health = shared.m_food * f; ... m_stamina ... m_eitr ...
    ///      if (m_time &lt;= 0) { message; m_foods.Remove(food); break; }
    ///      GetTotalFoodValue(...); SetMaxHealth(hp, true); SetMaxStamina(..); SetMaxEitr(..);</c>
    ///
    /// The transpiler swaps that single <c>call Mathf.Pow(float,float)</c> for
    /// <see cref="DecayFraction"/>, which returns <c>max(pow(frac, CurveExponent), KeepFraction)</c>.
    /// Everything else - the timer, the -1s decrement, expiry removal and message, the totals, the
    /// exact cadence of SetMaxHealth/SetMaxStamina/SetMaxEitr - is untouched vanilla code, so the
    /// module is behaviourally identical to vanilla except for the fraction itself.
    ///
    /// WHY it changed (0.4.1 bug, reported from a real client): the old implementation was a
    /// POSTFIX. Vanilla lowered each food's value and pushed the lowered totals through
    /// SetMaxHealth/SetMaxStamina/SetMaxEitr; the postfix then raised them and pushed the raised
    /// totals through the same three setters again, once per second, forever. Player.SetMaxHealth
    /// (Player.cs:5700) and its siblings call <c>Hud.instance.FlashHealthBar()</c> /
    /// <c>StaminaBarUppgradeFlash()</c> / <c>EitrBarUppgradeFlash()</c> whenever the new max is
    /// GREATER than the current one - which, after vanilla had just lowered it, was true on every
    /// single tick. That is exactly the "bars constantly pulse, lowering and filling up" Matt saw:
    /// a max value oscillating within one frame, plus an upgrade flash every second. With the
    /// decay removed at the source the totals never move, <c>health &gt; GetMaxHealth()</c> is
    /// false, and the bars are static.
    ///
    /// The food ICONS pulse for a different, purely cosmetic reason, in Hud.UpdateFood
    /// (Hud.decompiled.cs:890): the icon alpha is driven by <c>0.7 + sin(Time.time * 5) * 0.3</c>
    /// whenever <c>food.CanEatAgain()</c> (i.e. <c>m_time &lt; burnTime / 2</c>), and the remaining
    /// time text by <c>0.4 + sin(Time.time * 10) * 0.6</c> whenever <c>m_time &lt; 60</c>. Neither
    /// reads m_health/m_stamina/m_eitr, so the transpiler cannot quiet them; <see cref="HidePulse"/>
    /// does, with a postfix that paints both back to solid white while the food still has time
    /// left. <c>PulseBelowSeconds</c> (default 0 = never) keeps the vanilla pulse as a genuine
    /// "about to run out" warning below that many seconds.
    /// </summary>
    internal sealed class FoodNoDecayModule : FeatureModule
    {
        public override string Name => "FoodNoDecay";

        /// <summary>
        /// Normally Client (the shipping default). Setting the machine-local [Food] SelfTest = true
        /// flips this to Both so the headless test server (0 players) can prove the ObjectDB lookup
        /// and the fraction maths on its own - the patches themselves still gate on ClientActive()
        /// and stay inert there. Safe to key off the config value: Plugin.Awake calls Configure()
        /// (which runs Bind()) before TryEnable() reads Side. Same trick as VanguardShadow (§13.3).
        /// </summary>
        public override ModuleSide Side => _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        public override string Section => "Food";

        private static ConfigEntry<float> _keepFraction;
        private static ConfigEntry<float> _curveExponent;
        private static ConfigEntry<bool> _hidePulse;
        private static ConfigEntry<float> _pulseBelowSeconds;
        private static ConfigEntry<bool> _selfTest;
        private static FoodNoDecayModule _self;

        /// <summary>
        /// True only while the local player's own UpdateFood is on the stack. The transpiled call
        /// site cannot see `this`, so the prefix records it instead - Unity runs all of this on one
        /// thread and UpdateFood is not re-entrant, so this is exact.
        /// </summary>
        private static bool _localTick;

        /// <summary>HidePulse gives up after three consecutive HUD failures rather than spamming.</summary>
        private static int _pulseErrors;

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
                "Exponent used for the vanilla decay curve before the KeepFraction floor is " +
                "applied. Leave at 0.3 (vanilla's own curve) unless you specifically want a " +
                "different decay shape for the portion below KeepFraction.");

            _hidePulse = BindSynced("HidePulse", true,
                "Stop the food icons and their timers flashing in the HUD. Vanilla pulses an " +
                "icon once the food is past half its timer and flashes its countdown under a " +
                "minute; with decay removed that flashing is telling you about a decay that no " +
                "longer happens. See PulseBelowSeconds to keep it as a last-seconds warning.");

            _pulseBelowSeconds = BindSynced("PulseBelowSeconds", 0f,
                "When HidePulse is on, still let a food icon pulse once it has fewer than this " +
                "many seconds left, as an 'about to run out' warning. 0 (default) = never pulse.");

            _selfTest = BindLocal("SelfTest", false,
                "Local debug only, not synced. When true, on (re)load and on Enabled toggling logs " +
                "the decay fraction across three consecutive simulated ticks for CookedMeat, " +
                "asserting it does not move. Leave false in normal play.");
        }

        protected override void ApplyPatches()
        {
            var update = AccessTools.Method(typeof(Player), "UpdateFood", new[] { typeof(float), typeof(bool) });
            if (update == null) throw new Exception("Player.UpdateFood(float,bool) not found");
            Harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(UpdateFoodPre)),
                postfix: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(UpdateFoodPost)),
                transpiler: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(UpdateFoodTranspiler)));

            var hudFood = AccessTools.Method(typeof(Hud), "UpdateFood", new[] { typeof(Player) });
            if (hudFood == null) throw new Exception("Hud.UpdateFood(Player) not found");
            Harmony.Patch(hudFood, postfix: new HarmonyMethod(typeof(FoodNoDecayModule), nameof(HudFoodPost)));

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

        // ---- the decay curve, replaced at the source ------------------------------------------------

        /// <summary>
        /// The fraction-of-full value a food is worth, given how much of its burn time is left.
        /// Pure function of config; shared by the live call site and SelfTest so they can never
        /// disagree. <c>frac</c> is vanilla's own <c>Clamp01(m_time / m_foodBurnTime)</c>.
        /// </summary>
        internal static float Fraction(float frac)
        {
            float curved = Mathf.Pow(frac, _curveExponent != null ? _curveExponent.Value : 0.3f);
            float floor = Mathf.Clamp01(_keepFraction != null ? _keepFraction.Value : 1f);
            return Mathf.Max(curved, floor);
        }

        /// <summary>
        /// Drop-in replacement for the <c>Mathf.Pow(f, 0.3f)</c> inside Player.UpdateFood. When the
        /// module is inactive - or this is somehow not the local player's tick - it IS
        /// <c>Mathf.Pow</c>, byte for byte, so an inert module leaves vanilla exactly as it was.
        /// </summary>
        public static float DecayFraction(float frac, float vanillaExponent)
        {
            if (!_localTick || !Live()) return Mathf.Pow(frac, vanillaExponent);
            return Fraction(frac);
        }

        private static void UpdateFoodPre(Player __instance)
        {
            _localTick = __instance != null && __instance == Player.m_localPlayer;
        }

        private static void UpdateFoodPost()
        {
            _localTick = false;
        }

        /// <summary>
        /// Swap the single <c>call Mathf.Pow(float32, float32)</c> in Player.UpdateFood for
        /// <see cref="DecayFraction"/>. Same signature, same stack shape, so nothing else in the
        /// method body moves. Exactly one match is required: a game update that adds or removes a
        /// Pow call here must fail loudly (the module reports FAILED in the summary) rather than
        /// silently patch the wrong maths.
        /// </summary>
        private static IEnumerable<CodeInstruction> UpdateFoodTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var mine = AccessTools.Method(typeof(FoodNoDecayModule), nameof(DecayFraction));
            if (mine == null) throw new Exception("FoodNoDecayModule.DecayFraction not found");

            var code = new List<CodeInstruction>(instructions);
            int hits = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call) continue;
                // Matched by declaring type + name + signature rather than by MethodInfo identity:
                // the operand handed to a transpiler comes from reading the IL, and need not be the
                // same object AccessTools.Method would hand back.
                var mi = code[i].operand as MethodInfo;
                if (mi == null || mi.DeclaringType != typeof(Mathf) || mi.Name != "Pow") continue;
                var ps = mi.GetParameters();
                if (ps.Length != 2 || ps[0].ParameterType != typeof(float) || ps[1].ParameterType != typeof(float)) continue;
                code[i].operand = mine;
                hits++;
            }
            if (hits != 1)
                throw new Exception("Player.UpdateFood: expected exactly 1 Mathf.Pow call to replace, found " + hits);

            Log.LogInfo("[FoodNoDecay] UpdateFood decay curve replaced (1 call site)");
            return code;
        }

        // ---- the HUD pulse ---------------------------------------------------------------------------

        /// <summary>
        /// Postfix for Hud.UpdateFood(Player). Vanilla has just written a sine-wave alpha into the
        /// food icon (<c>0.7 + sin(t*5)*0.3</c> when <c>CanEatAgain()</c>, i.e. past half the
        /// timer) and into the countdown text (<c>0.4 + sin(t*10)*0.6</c> under a minute). Both are
        /// pure functions of m_time, so the only way to quiet them is to paint over the result.
        /// Everything else the HUD does with food - icon sprite, the bar widths, the countdown text
        /// itself - is left alone.
        /// </summary>
        private static void HudFoodPost(Hud __instance, Player player)
        {
            if (!Live() || _hidePulse == null || !_hidePulse.Value || _pulseErrors >= 3) return;
            if (__instance == null || player == null) return;
            if (__instance.m_foodIcons == null || __instance.m_foodTime == null) return;

            try
            {
                var foods = player.GetFoods();
                if (foods == null) return;
                float warnBelow = _pulseBelowSeconds != null ? _pulseBelowSeconds.Value : 0f;

                int n = Mathf.Min(__instance.m_foodIcons.Length, __instance.m_foodTime.Length);
                for (int i = 0; i < n && i < foods.Count; i++)
                {
                    var food = foods[i];
                    if (food == null) continue;
                    // Below the warning threshold vanilla's flashing is left exactly as it is - that
                    // is a real "this is about to run out" signal, not a decay indicator.
                    if (warnBelow > 0f && food.m_time < warnBelow) continue;

                    var icon = __instance.m_foodIcons[i];
                    if (icon != null) icon.color = Color.white;
                    var text = __instance.m_foodTime[i];
                    if (text != null) text.color = Color.white;
                }
            }
            catch (Exception e)
            {
                // Never touch the synced config from here - just stop trying after three failures
                // and leave the vanilla HUD to do whatever it does.
                if (++_pulseErrors <= 3)
                    Log.LogWarning("[FoodNoDecay] HidePulse failed (" + _pulseErrors + "/3): " + e.Message);
            }
        }

        // ---- reporting -----------------------------------------------------------------------------

        private string Numbers()
        {
            return "KeepFraction=" + (_keepFraction != null ? _keepFraction.Value.ToString("0.###") : "?") +
                   " CurveExponent=" + (_curveExponent != null ? _curveExponent.Value.ToString("0.###") : "?") +
                   " HidePulse=" + (_hidePulse != null && _hidePulse.Value) +
                   " PulseBelowSeconds=" + (_pulseBelowSeconds != null ? _pulseBelowSeconds.Value.ToString("0.###") : "?");
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
        /// Headless proof. Takes CookedMeat out of ObjectDB, starts it at 10% of its burn time and
        /// runs THREE consecutive simulated vanilla ticks (the same `m_time -= 1` + fraction the
        /// transpiled method now runs), logging what vanilla would have produced against what the
        /// module produces - and asserting the module's values do not move across the three ticks.
        /// A moving value is precisely the 0.4.1 bug that made the bars pulse.
        /// </summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][FoodNoDecay] ").Append(_self != null ? _self.Numbers() : "(unbound)");

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

            float burn = shared.m_foodBurnTime;
            float time = burn * 0.1f;
            sb.Append("\n  CookedMeat burnTime=").Append(burn).Append(" starting at ").Append(time)
              .Append("s (10% remaining), 3 consecutive 1s ticks:");

            float firstHealth = 0f, firstStamina = 0f, firstEitr = 0f;
            bool stable = true;
            for (int tick = 1; tick <= 3; tick++)
            {
                time -= 1f;                                    // exactly what vanilla's loop does
                float frac = Mathf.Clamp01(time / burn);

                float vanilla = Mathf.Pow(frac, 0.3f);         // vanilla's own hardcoded exponent
                float kept = Fraction(frac);                   // what the transpiled call site returns

                float h = shared.m_food * kept;
                float s = shared.m_foodStamina * kept;
                float e = shared.m_foodEitr * kept;

                if (tick == 1) { firstHealth = h; firstStamina = s; firstEitr = e; }
                else if (h != firstHealth || s != firstStamina || e != firstEitr) stable = false;

                sb.Append("\n    tick ").Append(tick).Append(" t=").Append(time)
                  .Append("  vanilla f=").Append(vanilla.ToString("0.#####"))
                  .Append(" -> health=").Append((shared.m_food * vanilla).ToString("0.####"))
                  .Append("  |  kept f=").Append(kept.ToString("0.#####"))
                  .Append(" -> health=").Append(h.ToString("0.####"))
                  .Append(" stamina=").Append(s.ToString("0.####"))
                  .Append(" eitr=").Append(e.ToString("0.####"));
            }

            sb.Append("\n  ").Append(stable ? "PASS" : "FAIL")
              .Append("  the kept values are identical across all 3 ticks (no oscillation, so ")
              .Append("SetMaxHealth/SetMaxStamina/SetMaxEitr never see a rising max and never flash the bars)");

            // A KeepFraction below 1 must still let vanilla's curve run down to the floor, so prove
            // the floor is a floor rather than a freeze: at 0% time left the value IS the floor.
            float floor = Mathf.Clamp01(_keepFraction != null ? _keepFraction.Value : 1f);
            float atZero = Fraction(0f);
            sb.Append("\n  ").Append(Mathf.Abs(atZero - floor) < 0.0001f ? "PASS" : "FAIL")
              .Append("  at 0 time left the fraction is the KeepFraction floor (").Append(atZero.ToString("0.####"))
              .Append(" vs ").Append(floor.ToString("0.####")).Append(")");

            return sb.ToString();
        }
    }
}
