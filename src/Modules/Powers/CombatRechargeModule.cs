using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **CombatRecharge** - fighting shortens your Forsaken power cooldowns.
    ///
    /// Every hit you land takes SecondsPerHitDealt off every power cooldown you are carrying, and
    /// every hit you take takes SecondsPerHitTaken off. MaxPerSecond caps the total reduction in
    /// any one real second, so a multi-target swing, a burning DoT or an arrow storm cannot dump a
    /// five-minute cooldown in one frame.
    ///
    /// Where the hits are caught
    /// -------------------------
    /// * DEALT: postfix on `Character.Damage(HitData)`. That method runs on the ATTACKER's client -
    ///   it is the caller that does `m_nview.InvokeRPC("RPC_Damage", hit)` (Character.decompiled.cs
    ///   :1884). Melee, bows and staves all funnel through it (Projectile.OnHit ends in
    ///   character.Damage), so one patch covers every weapon. `__instance` is the VICTIM; we keep
    ///   the hit when `hit.GetAttacker() == Player.m_localPlayer`, the victim is not us, and
    ///   `hit.GetTotalDamage() > 0`.
    /// * TAKEN: postfix on `Player.OnDamaged(HitData)`, which vanilla runs on the owner of the
    ///   character being hurt (Character.RPC_Damage -> ApplyDamage -> OnDamaged), i.e. on our own
    ///   client for our own player, once, after the damage actually applied.
    ///
    /// The module is independent of DualPowers: it always shortens the vanilla cooldown itself and
    /// only asks DualPowersModule.ReduceCooldowns for the extra slots, which is a no-op when
    /// DualPowers is off, set to 1 slot, or running shared cooldowns.
    /// </summary>
    internal sealed class CombatRechargeModule : FeatureModule
    {
        public override string Name => "CombatRecharge";
        public override string Section => "Recharge";
        public override ModuleSide Side => ModuleSide.Client;

        private static CombatRechargeModule _inst;

        private ConfigEntry<float> _perHitDealt;
        private ConfigEntry<float> _perHitTaken;
        private ConfigEntry<float> _maxPerSecond;
        private ConfigEntry<bool> _affectAllSlots;
        private ConfigEntry<bool> _countPlayerTargets;
        private ConfigEntry<bool> _showMessages;

        // rate cap: one rolling one-second window
        private static float _windowStart;
        private static float _spentThisWindow;

        // counters for nvlb.status
        private static int _hitsDealt;
        private static int _hitsTaken;
        private static float _totalGranted;
        private static float _totalClipped;
        private static int _errors;

        protected override void Bind()
        {
            _perHitDealt = BindSynced("SecondsPerHitDealt", 2f,
                "Seconds taken off your power cooldowns for every hit you land.");
            _perHitTaken = BindSynced("SecondsPerHitTaken", 3f,
                "Seconds taken off your power cooldowns for every hit you take.");
            _maxPerSecond = BindSynced("MaxPerSecond", 10f,
                "Hard cap on how many cooldown seconds one real second of combat can remove. " +
                "Stops multi-hit AoE and damage-over-time from emptying a cooldown instantly.");
            _affectAllSlots = BindSynced("AffectAllSlots", true,
                "Also shorten the DualPowers extra slots. False = only the vanilla slot 1 cooldown.");
            _countPlayerTargets = BindSynced("CountPlayerTargets", false,
                "Count hits you land on other players (PvP) as well as on creatures.");
            _showMessages = BindLocal("ShowMessages", false,
                "Machine-local. Pop a small message every time a hit shortens a cooldown. " +
                "Noisy - for tuning only.");
            _inst = this;
        }

        protected override void ApplyPatches()
        {
            var damage = AccessTools.Method(typeof(Character), "Damage", new[] { typeof(HitData) });
            if (damage == null) throw new Exception("Character.Damage(HitData) not found");
            var onDamaged = AccessTools.Method(typeof(Player), "OnDamaged", new[] { typeof(HitData) });
            if (onDamaged == null) throw new Exception("Player.OnDamaged(HitData) not found");
            if (AccessTools.Method(typeof(HitData), "GetAttacker") == null)
                throw new Exception("HitData.GetAttacker() not found");
            if (AccessTools.Method(typeof(HitData), "GetTotalDamage") == null)
                throw new Exception("HitData.GetTotalDamage() not found");

            Harmony.Patch(damage, postfix: new HarmonyMethod(typeof(CombatRechargeModule), nameof(DamagePostfix)));
            Harmony.Patch(onDamaged, postfix: new HarmonyMethod(typeof(CombatRechargeModule), nameof(OnDamagedPostfix)));

            Log.LogInfo("[CombatRecharge] dealt=-" + _perHitDealt.Value + "s taken=-" + _perHitTaken.Value +
                        "s cap=" + _maxPerSecond.Value + "s/s allSlots=" + _affectAllSlots.Value +
                        " countPlayerTargets=" + _countPlayerTargets.Value);
        }

        // ---- hooks ------------------------------------------------------------------------

        /// <summary>Character.Damage runs on the attacker's client: this is a hit WE landed.</summary>
        private static void DamagePostfix(Character __instance, HitData hit)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            try
            {
                var me = Player.m_localPlayer;
                if (me == null || hit == null || __instance == null) return;
                if (__instance == me) return;                              // self-damage is "taken", not "dealt"
                if (hit.GetAttacker() != me) return;
                if (!_inst._countPlayerTargets.Value && __instance.IsPlayer()) return;
                if (hit.GetTotalDamage() <= 0f) return;

                _hitsDealt++;
                Apply(me, _inst._perHitDealt.Value, "hit");
            }
            catch (Exception e)
            {
                if (_errors++ < 3) Log.LogError("[CombatRecharge] dealt hook failed (" + _errors + "/3): " + e);
            }
        }

        /// <summary>Player.OnDamaged runs on the owner: this is a hit WE took.</summary>
        private static void OnDamagedPostfix(Player __instance, HitData hit)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            try
            {
                var me = Player.m_localPlayer;
                if (me == null || hit == null || __instance != me) return;
                if (hit.GetTotalDamage() <= 0f) return;

                _hitsTaken++;
                Apply(me, _inst._perHitTaken.Value, "hurt");
            }
            catch (Exception e)
            {
                if (_errors++ < 3) Log.LogError("[CombatRecharge] taken hook failed (" + _errors + "/3): " + e);
            }
        }

        private static void Apply(Player me, float seconds, string why)
        {
            if (seconds <= 0f) return;
            float granted = Grant(seconds, _inst._maxPerSecond.Value, Time.unscaledTime);
            if (granted <= 0f) return;

            me.m_guardianPowerCooldown = Mathf.Max(0f, me.m_guardianPowerCooldown - granted);
            if (_inst._affectAllSlots.Value) DualPowersModule.ReduceCooldowns(granted);

            if (_inst._showMessages.Value)
                me.Message(MessageHud.MessageType.TopLeft,
                    "Power recharge " + why + ": -" + granted.ToString("0.#") + "s");
        }

        // ---- the rate cap (pure, so the self test can drive it) -------------------------------

        /// <summary>
        /// Book a reduction against the rolling one-second window. Returns how much of
        /// <paramref name="want"/> was actually allowed. A cap &lt;= 0 means "no cap".
        /// </summary>
        public static float Grant(float want, float maxPerSecond, float now)
        {
            if (want <= 0f) return 0f;
            if (maxPerSecond <= 0f) { _totalGranted += want; return want; }

            if (now - _windowStart >= 1f || now < _windowStart)
            {
                _windowStart = now;
                _spentThisWindow = 0f;
            }
            float room = Mathf.Max(0f, maxPerSecond - _spentThisWindow);
            float give = Mathf.Min(want, room);
            _spentThisWindow += give;
            _totalGranted += give;
            _totalClipped += (want - give);
            return give;
        }

        /// <summary>Reset the rate-limit window (self test / config change).</summary>
        public static void ResetWindow()
        {
            _windowStart = 0f;
            _spentThisWindow = 0f;
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ResetWindow();
        }

        public override string StatusDetail()
        {
            if (_perHitDealt == null) return null;
            return "dealt=-" + _perHitDealt.Value + "s taken=-" + _perHitTaken.Value +
                   "s cap=" + _maxPerSecond.Value + "s/s allSlots=" + _affectAllSlots.Value +
                   "  hits=" + _hitsDealt + "/" + _hitsTaken +
                   " granted=" + _totalGranted.ToString("0.#") + "s clipped=" + _totalClipped.ToString("0.#") + "s";
        }

        // ---- self test -------------------------------------------------------------------------

        /// <summary>
        /// Drive the real cap maths with no game state: <paramref name="dealt"/> hits landed and
        /// <paramref name="taken"/> hits received against a cooldown of <paramref name="cooldown"/>
        /// seconds, first all inside one real second (the cap bites) and then one per second (it
        /// does not). Called by DualPowers' SelfTest so the whole feature pair proves itself on a
        /// dedicated server with no players connected.
        /// </summary>
        public static void LogSelfTest(float cooldown, int dealt, int taken)
        {
            float perDealt = (_inst != null && _inst._perHitDealt != null) ? _inst._perHitDealt.Value : 2f;
            float perTaken = (_inst != null && _inst._perHitTaken != null) ? _inst._perHitTaken.Value : 3f;
            float cap = (_inst != null && _inst._maxPerSecond != null) ? _inst._maxPerSecond.Value : 10f;

            float wanted = dealt * perDealt + taken * perTaken;

            // burst: every hit inside the same real second.
            ResetWindow();
            float cd = cooldown;
            float given = 0f;
            for (int i = 0; i < dealt; i++) { float g = Grant(perDealt, cap, 100f); given += g; cd = Mathf.Max(0f, cd - g); }
            for (int i = 0; i < taken; i++) { float g = Grant(perTaken, cap, 100f); given += g; cd = Mathf.Max(0f, cd - g); }

            Log.LogInfo("[CombatRecharge] SelfTest: " + dealt + " hits dealt (-" + perDealt + "s each) + " +
                        taken + " taken (-" + perTaken + "s each) = " + wanted + "s wanted, cap " + cap + "s/s");
            Log.LogInfo("[CombatRecharge] SelfTest: burst (all in one second): granted " + given +
                        "s, cooldown " + cooldown + "s -> " + cd + "s" +
                        (Mathf.Abs(given - Mathf.Min(wanted, cap)) < 0.001f ? "  OK" : "  *** FAIL ***"));

            // spread: one hit per second, the cap never bites.
            ResetWindow();
            float cd2 = cooldown;
            float given2 = 0f;
            float t = 1000f;
            for (int i = 0; i < dealt; i++) { float g = Grant(perDealt, cap, t); t += 1f; given2 += g; cd2 = Mathf.Max(0f, cd2 - g); }
            for (int i = 0; i < taken; i++) { float g = Grant(perTaken, cap, t); t += 1f; given2 += g; cd2 = Mathf.Max(0f, cd2 - g); }

            Log.LogInfo("[CombatRecharge] SelfTest: spread (one hit per second): granted " + given2 +
                        "s, cooldown " + cooldown + "s -> " + cd2 + "s" +
                        (Mathf.Abs(given2 - wanted) < 0.001f ? "  OK" : "  *** FAIL ***"));
            ResetWindow();
            _totalGranted = 0f; _totalClipped = 0f;   // the self test must not pollute nvlb.status
        }
    }
}
