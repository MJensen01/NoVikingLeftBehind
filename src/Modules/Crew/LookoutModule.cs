using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Stand off the tiller on a moving ship and you see further - the map reveals wider for the
    /// lookout.
    ///
    /// That is the whole module. No broadcast line, no automatic pins, no serpent callout, no
    /// voyage stat: extra fog radius for the local player and nothing else. It is therefore
    /// completely client-local - it writes no ZDO, sends no RPC and shares no state - so two
    /// clients running different settings, or one running none at all, can never disagree about
    /// anything. The only thing it can touch is the local player's own explored-map texture,
    /// which vanilla already treats as per-player data.
    ///
    /// Patch point - <c>Minimap.UpdateExplore(float dt, Player player)</c> (0.221.13 decompile):
    ///
    ///     private void UpdateExplore(float dt, Player player)
    ///     {
    ///         m_exploreTimer += Time.deltaTime;
    ///         if (m_exploreTimer > m_exploreInterval)
    ///         {
    ///             m_exploreTimer = 0f;
    ///             Explore(player.transform.position, m_exploreRadius);
    ///         }
    ///     }
    ///
    /// The radius is read straight off the field, so the cheapest correct implementation is to
    /// scale <c>m_exploreRadius</c> for the duration of that one call and put it back afterwards.
    /// The alternative - a postfix that calls <c>Explore()</c> a second time with a bigger radius -
    /// was rejected: <c>UpdateExplore</c> runs every frame while only the timer decides whether
    /// <c>Explore</c> (an O(r^2) pixel loop) actually fires, so a postfix would either re-run that
    /// loop 60x a second or have to re-derive the timer decision.
    ///
    /// Leaking the scaled value into another caller is the one real risk, and it is closed three
    /// ways: the prefix saves and the postfix restores the exact float it saved (never a constant);
    /// the prefix self-heals first, restoring a value left behind by a call that somehow never
    /// reached its postfix; and <c>m_exploreRadius</c> is read from exactly one place in the whole
    /// game (this method - checked across the decompiled assemblies), so even a leaked frame could
    /// only ever affect the next explore tick. <c>Minimap.Update</c> is the sole caller and the
    /// main thread is the sole thread, so the save/restore pair cannot interleave with itself.
    ///
    /// Never <c>ExploreAll</c>, never a pin, never anything but the fog radius. The helmsman
    /// (<c>player.GetControlledShip() != null</c>) always gets the vanilla radius - the bonus is
    /// for the person who is NOT driving. There is no "stand at the bow" rule and no role system:
    /// anyone aboard who is not steering is the lookout.
    /// </summary>
    internal sealed class LookoutModule : FeatureModule
    {
        public override string Name => "Lookout";

        public override string Theme => "On the water";

        public override string Hint => "See further while riding a ship you are not steering";

        /// <summary>Client only - a dedicated server has no Minimap and no local player.</summary>
        public override ModuleSide Side => ModuleSide.Client;

        private const float MinMultiplier = 1.0f;
        private const float MaxMultiplier = 3.0f;

        private ConfigEntry<float> _multiplier;
        private ConfigEntry<bool> _requireMoving;
        private ConfigEntry<float> _minSpeed;

        private static LookoutModule _self;

        // Save/restore state for the one in-flight UpdateExplore call. Main thread only; see the
        // class doc for why a static is safe (and preferable to __state, which would have to be
        // threaded through a finalizer to survive a throw).
        private static bool _scaled;
        private static float _savedRadius;

        // One throw in a per-frame path logs once and stops the module for the session rather
        // than spamming the log 60x a second.
        private static bool _broken;

        private static bool Live()
        {
            return !_broken && _self != null && _self.Active && ClientActive();
        }

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _multiplier = BindSynced("RadiusMultiplier", 1.75f,
                "How much wider the map reveals for a crewmate who is aboard a moving ship and " +
                "NOT at the tiller. 1.0 = vanilla (feature off). Clamped to " +
                MinMultiplier.ToString("0.0") + "-" + MaxMultiplier.ToString("0.0") + ".",
                Opt.N("How much wider the map reveals for a lookout not at the tiller",
                    MinMultiplier, MaxMultiplier, 0.05));

            _requireMoving = BindSynced("RequireMoving", true,
                "Only widen the radius while the ship is actually under way. Off = anyone " +
                "standing on a ship gets the wider radius, moored included.",
                Opt.B("Only widen the map reveal while the ship is moving"));

            _minSpeed = BindSynced("MinSpeed", 1.0f,
                "Ship speed (m/s along its own forward axis, from Ship.GetSpeed(), sign " +
                "ignored) at or above which the ship counts as under way. Only used when " +
                "RequireMoving is on.",
                Opt.N("How fast the ship must go to count as under way", 0, 10, 0.5));
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            // A live change takes effect on the next explore tick; nothing to recompute. If the
            // module was just turned off mid-call there is nothing to undo either - the postfix
            // restores unconditionally on the flag, not on Active.
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private float Multiplier()
        {
            float m = _multiplier != null ? _multiplier.Value : 1f;
            if (float.IsNaN(m)) return 1f;
            return Mathf.Clamp(m, MinMultiplier, MaxMultiplier);
        }

        // ---- patches --------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var updateExplore = AccessTools.Method(typeof(Minimap), "UpdateExplore",
                new[] { typeof(float), typeof(Player) });
            if (updateExplore == null)
                throw new Exception("Minimap.UpdateExplore(float, Player) not found");

            if (AccessTools.Field(typeof(Minimap), "m_exploreRadius") == null)
                throw new Exception("Minimap.m_exploreRadius not found");

            Harmony.Patch(updateExplore,
                prefix: new HarmonyMethod(typeof(LookoutModule), nameof(UpdateExplorePre)),
                postfix: new HarmonyMethod(typeof(LookoutModule), nameof(UpdateExplorePost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private static void UpdateExplorePre(Minimap __instance, Player player)
        {
            // Self-heal: a previous call that never reached its postfix (an exception inside
            // Explore) must not leave the scaled radius behind.
            if (_scaled && __instance != null)
            {
                __instance.m_exploreRadius = _savedRadius;
                _scaled = false;
            }

            if (!Live() || __instance == null || player == null) return;

            try
            {
                // Only ever the local player's own fog. UpdateExplore is called with
                // Player.m_localPlayer in vanilla; this makes that explicit rather than assumed.
                if (Player.m_localPlayer == null || player != Player.m_localPlayer) return;

                float mult = _self.Multiplier();
                if (mult <= 1.0001f) return;

                // The one at the tiller sails vanilla.
                if (player.GetControlledShip() != null) return;

                var ship = CrewShip.Aboard(player);
                if (ship == null) return;

                if (_self._requireMoving != null && _self._requireMoving.Value)
                {
                    float minSpeed = _self._minSpeed != null ? _self._minSpeed.Value : 1f;
                    if (minSpeed < 0f) minSpeed = 0f;
                    if (Mathf.Abs(ship.GetSpeed()) < minSpeed) return;
                }

                _savedRadius = __instance.m_exploreRadius;
                _scaled = true;
                __instance.m_exploreRadius = _savedRadius * mult;
            }
            catch (Exception e)
            {
                if (_scaled && __instance != null)
                {
                    __instance.m_exploreRadius = _savedRadius;
                    _scaled = false;
                }
                _broken = true;
                Log.LogError("[Lookout] disabled for this session after an error in " +
                             "Minimap.UpdateExplore prefix: " + e);
            }
        }

        private static void UpdateExplorePost(Minimap __instance)
        {
            // Deliberately NOT gated on Live(): if the prefix scaled the radius, it is restored
            // no matter what happened to the config in between.
            if (!_scaled) return;
            _scaled = false;
            if (__instance != null) __instance.m_exploreRadius = _savedRadius;
        }

        // ---- reporting ------------------------------------------------------------------------

        private string Numbers()
        {
            return "RadiusMultiplier=" + Multiplier().ToString("0.00") +
                   " RequireMoving=" + (_requireMoving != null && _requireMoving.Value) +
                   " MinSpeed=" + (_minSpeed != null ? _minSpeed.Value : 0f).ToString("0.00") +
                   " (local player only, never the helmsman; fog radius only)";
        }

        public override string StatusDetail() { return Numbers(); }
    }
}
