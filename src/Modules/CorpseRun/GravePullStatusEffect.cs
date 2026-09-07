using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// "Grave Pull" - the custom StatusEffect behind the GravePull half of CorpseRunPlus.
    ///
    /// A player whose corpse is a long way off gets a stamina tailwind that scales with the
    /// remaining distance, so the run back is a run and not a slog. Mobs are untouched: the
    /// effect only ever changes the local player's own stamina numbers.
    ///
    /// Everything is delivered through vanilla StatusEffect virtuals (verified in the 0.221.12
    /// decompile), so the module needs no Harmony patch on the stamina paths at all:
    ///
    ///   ModifyStaminaRegen(ref float)               &lt;- SEMan.ModifyStaminaRegen (SEMan:362)
    ///   ModifyRunStaminaDrain(base, ref d, dir)     &lt;- SEMan.ModifyRunStaminaDrain (SEMan:418)
    ///   ModifyJumpStaminaUsage(base, ref use)       &lt;- SEMan.ModifyJumpStaminaUsage (SEMan:430)
    ///
    /// Note the sign convention difference from SE_Stats: SE_Stats *adds*
    /// `baseDrain * m_runStaminaDrainModifier` to the accumulator, while we *scale* the
    /// accumulated value, which is what the spec asks for ("x (1 - MaxDrainReduction * s)").
    /// Scaling composes correctly with anything else that has already contributed.
    ///
    /// Strength is recomputed once a second by the module and pushed into the static fields, so
    /// a server config push retunes an already-applied buff with no re-apply.
    /// </summary>
    internal sealed class GravePullStatusEffect : StatusEffect
    {
        public const string SeName = "NVLB_GravePull";

        /// <summary>0..1 ramp from PullMinDistance to PullMinDistance + PullFullDistance.</summary>
        public static float Strength;
        /// <summary>Extra stamina regen at s = 1 (1.0 = +100%).</summary>
        public static float MaxRegenBonus;
        /// <summary>Fraction of run/jump stamina cost removed at s = 1 (0.5 = half price).</summary>
        public static float MaxDrainReduction;
        /// <summary>Metres left to the grave, for the icon text and the tooltip.</summary>
        public static float Distance;

        public static GravePullStatusEffect Create()
        {
            var se = CreateInstance<GravePullStatusEffect>();
            se.name = SeName;                       // NameHash() hashes base.name - set it first.
            se.m_name = "Grave Pull";
            se.m_category = "";
            se.m_flashIcon = false;
            se.m_cooldownIcon = false;
            se.m_ttl = 0f;                          // set by the module from PullUpdateSec
            se.m_tooltip = "";
            se.m_startMessageType = MessageHud.MessageType.TopLeft;
            se.m_stopMessageType = MessageHud.MessageType.TopLeft;
            return se;
        }

        private static float RegenMul()
        {
            float m = 1f + MaxRegenBonus * Strength;
            return m < 0.01f ? 0.01f : m;
        }

        private static float CostMul()
        {
            float m = 1f - MaxDrainReduction * Strength;
            if (m < 0.05f) m = 0.05f;
            if (m > 1f) m = 1f;
            return m;
        }

        /// <summary>The HUD icon shows the current pull strength instead of a countdown - the
        /// ttl is only a safety net and the module refreshes it every tick.</summary>
        public override string GetIconText()
        {
            return Mathf.RoundToInt(Strength * 100f) + "%";
        }

        public override string GetTooltipString()
        {
            return "The pull of your own grave.\n" +
                   "Strength <color=orange>" + Mathf.RoundToInt(Strength * 100f) + "%</color>" +
                   " (" + Mathf.RoundToInt(Distance) + " m to go)\n" +
                   "$se_staminaregen: <color=orange>+" +
                   Mathf.RoundToInt(MaxRegenBonus * Strength * 100f) + "%</color>\n" +
                   "$se_runstamina: <color=orange>-" +
                   Mathf.RoundToInt(MaxDrainReduction * Strength * 100f) + "%</color>\n" +
                   "$se_jumpstamina: <color=orange>-" +
                   Mathf.RoundToInt(MaxDrainReduction * Strength * 100f) + "%</color>";
        }

        public override void ModifyStaminaRegen(ref float staminaRegen)
        {
            if (Strength <= 0f || MaxRegenBonus == 0f) return;
            staminaRegen *= RegenMul();
        }

        public override void ModifyRunStaminaDrain(float baseDrain, ref float drain, Vector3 dir)
        {
            if (Strength <= 0f || MaxDrainReduction == 0f) return;
            drain *= CostMul();
        }

        public override void ModifyJumpStaminaUsage(float baseStaminaUse, ref float staminaUse)
        {
            if (Strength <= 0f || MaxDrainReduction == 0f) return;
            staminaUse *= CostMul();
        }
    }
}
