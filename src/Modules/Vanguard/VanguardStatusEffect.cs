using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// "Vanguard's Shadow" - the custom StatusEffect behind the VanguardShadow module.
    ///
    /// Every effect is delivered through a vanilla StatusEffect virtual, so the module needs
    /// no Harmony patch on the damage or skill paths at all (verified in the 0.221.12 decompile):
    ///
    ///   OnDamaged(hit, attacker)   <- SEMan.OnDamaged, called from Character.RPC_Damage
    ///                                 (Character.decompiled.cs:1923) on the OWNER of the damaged
    ///                                 character, i.e. the local client for the local player, and
    ///                                 BEFORE ApplyResistance / ApplyArmor / ApplyDamage. HitData
    ///                                 is a reference type, so hit.ApplyModifier() here really does
    ///                                 scale the incoming hit.
    ///   ModifyRaiseSkill(s, ref m) <- SEMan.ModifyRaiseSkill, called from Player.RaiseSkill
    ///                                 (Player.cs:1901) which multiplies the raise value by it.
    ///                                 Skills.RaiseSkill itself does NOT call it, so a mod that
    ///                                 prefixes Skills.RaiseSkill composes with us multiplicatively.
    ///   ModifyStaminaRegen(ref m)  <- SEMan.ModifyStaminaRegen, a multiplier on stamina regen.
    ///
    /// The numbers are read from the module's live config on every call (static fields), so a
    /// server config push takes effect on the already-applied clone with no re-apply.
    /// </summary>
    internal sealed class VanguardStatusEffect : StatusEffect
    {
        public const string SeName = "NVLB_Vanguard";

        /// <summary>Fraction of incoming damage removed (0.25 = take 75%).</summary>
        public static float DamageReduction;
        /// <summary>Extra skill XP as a fraction (0.5 = +50%).</summary>
        public static float XpBonus;
        /// <summary>Extra stamina regen as a fraction (0.2 = +20%).</summary>
        public static float StaminaRegen;

        public static VanguardStatusEffect Create()
        {
            var se = CreateInstance<VanguardStatusEffect>();
            se.name = SeName;                       // NameHash() hashes base.name - set it first.
            se.m_name = "Vanguard's Shadow";
            se.m_category = "";
            se.m_flashIcon = false;
            se.m_cooldownIcon = false;
            se.m_ttl = 0f;                          // set by the module from UpdateSec
            se.m_tooltip = "";                      // set by the module from the live config
            se.m_startMessageType = MessageHud.MessageType.TopLeft;
            se.m_stopMessageType = MessageHud.MessageType.TopLeft;
            return se;
        }

        /// <summary>
        /// The HUD icon would otherwise show m_ttl counting down. The ttl is only a safety net
        /// (the module re-applies every tick while eligible), so hide the number.
        /// </summary>
        public override string GetIconText()
        {
            return "";
        }

        public override void OnDamaged(HitData hit, Character attacker)
        {
            if (hit == null) return;
            float r = DamageReduction;
            if (r <= 0f) return;
            if (r > 0.95f) r = 0.95f;
            hit.ApplyModifier(1f - r);
        }

        public override void ModifyRaiseSkill(Skills.SkillType skill, ref float multiplier)
        {
            if (XpBonus == 0f) return;
            multiplier *= 1f + XpBonus;
        }

        public override void ModifyStaminaRegen(ref float staminaRegen)
        {
            if (StaminaRegen == 0f) return;
            staminaRegen *= 1f + StaminaRegen;
        }
    }
}
