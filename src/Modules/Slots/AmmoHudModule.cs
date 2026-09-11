using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **AmmoHud** - a quiet readout of what is in your ammo slots, bottom-left of the screen.
    ///
    /// ExtraSlots gives every player dedicated ammo slots (`[Slots] AmmoSlots`, 2 by default) and
    /// they are the right place to keep arrows - but until now the only way to know how many you
    /// had left was to open the bag mid-fight. This module draws one hotbar tile per non-empty ammo
    /// slot, showing the icon and the count, and marks the quiver you are actually shooting the way
    /// vanilla marks the equipped hotbar item.
    ///
    /// It ships no art. Every tile is an <c>Instantiate</c> of vanilla's own
    /// <c>HotkeyBar.m_elementPrefab</c> and the row is anchored by MEASURING vanilla's own HUD
    /// widgets, so it inherits Valheim's sprite, font, size and opacity and cannot overlap the
    /// health bar, the food icons, the status effects or the minimap - see <see cref="AmmoHudView"/>
    /// for both, and for the log lines every build step emits.
    ///
    /// What is patched:
    /// * <c>Hud.Update()</c> (postfix) - one tick: the visibility gate, the first-use build, and the
    ///   cheap per-frame refresh. It runs after vanilla's own <c>SetVisible(!m_userHidden &amp;&amp;
    ///   !localPlayer.InCutscene())</c> (Hud.cs:524), so the gate reads a settled HUD.
    /// * <c>Player.OnInventoryChanged()</c> (postfix) - the readout's only "something moved" signal.
    ///   The UI is NOT rebuilt per frame; this is what makes it re-read the slots.
    /// * <c>Player.OnSpawned(bool)</c> (postfix) - a fresh character, a fresh read.
    ///
    /// Side is Client: a dedicated server has no Hud at all. The synced <c>[AmmoHud] Enabled</c> is
    /// still bound on the server (FeatureModule.Configure runs regardless of Side), so the readout
    /// can be switched off server-wide for everyone; the offset, scale and opacity knobs are
    /// <c>BindLocal</c>, so each player positions it to taste from the in-game settings tab with no
    /// rebuild and no restart.
    /// </summary>
    internal sealed class AmmoHudModule : FeatureModule
    {
        public override string Name => "AmmoHud";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "AmmoHud";
        public override string Theme => "Inventory";
        public override string Hint => "Show your ammo slots on the HUD";

        private static AmmoHudModule _inst;
        private static int _tickErrors;

        private ConfigEntry<float> _offsetX;
        private ConfigEntry<float> _offsetY;
        private ConfigEntry<float> _scale;
        private ConfigEntry<float> _alpha;

        // ---- config ----------------------------------------------------------------------

        protected override void Bind()
        {
            _offsetX = BindLocal("OffsetX", 0f,
                "Machine-local. Pixels to nudge the ammo readout sideways from where the mod puts " +
                "it. 0 leaves it flush with the left edge of vanilla's own bottom-left HUD widgets " +
                "(the anchor it works out at start-up is written to the log). Positive moves right.",
                Opt.N("Nudge the ammo readout sideways, in pixels", -600, 600, 1));
            _offsetY = BindLocal("OffsetY", 0f,
                "Machine-local. Pixels to nudge the ammo readout up or down. 0 sits it just above " +
                "whatever vanilla already draws in the bottom-left corner, so it cannot overlap the " +
                "health bar, the food icons, the status effects or the minimap. Positive moves up.",
                Opt.N("Nudge the ammo readout up or down, in pixels", -600, 600, 1));
            _scale = BindLocal("Scale", 1f,
                "Machine-local. Size of the ammo readout relative to the hotbar (0.4-2.5). 1 draws " +
                "the tiles exactly the size of a hotbar slot, which is what makes it look native.",
                Opt.N("Size of the ammo readout, 1 matches the hotbar", 0.4, 2.5, 0.05));
            _alpha = BindLocal("Alpha", 0.9f,
                "Machine-local. Opacity of the ammo readout (0.1-1). The default sits it a touch " +
                "behind the hotbar so it reads as background information; the quiver you have " +
                "equipped is always drawn at full strength within that.",
                Opt.N("Opacity of the ammo readout", 0.1, 1.0, 0.05));

            _inst = this;
            Push();
        }

        private void Push()
        {
            AmmoHudView.Configure(new Vector2(_offsetX.Value, _offsetY.Value), _scale.Value, _alpha.Value);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            Push();

            // The slot layout itself lives in [Slots]; a change there is picked up by the view on
            // its own (it rebuilds when SlotLayout.AmmoCount stops matching what it built), so all
            // this has to handle is our own switch.
            if (ReferenceEquals(entry, EnabledCfg))
            {
                if (!Enabled) AmmoHudView.Destroy();
                else AmmoHudView.Reset();
            }
            AmmoHudView.Invalidate();
        }

        // ---- patches ---------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var self = typeof(AmmoHudModule);

            var hudUpdate = AccessTools.Method(typeof(Hud), "Update");
            var invChanged = AccessTools.Method(typeof(Player), "OnInventoryChanged");
            var spawned = AccessTools.Method(typeof(Player), "OnSpawned", new[] { typeof(bool) });

            Require(hudUpdate, "Hud.Update()");
            Require(invChanged, "Player.OnInventoryChanged()");
            Require(spawned, "Player.OnSpawned(bool)");

            // Read rather than patched - fail loudly here instead of drawing an empty tile later.
            if (AccessTools.Method(typeof(Humanoid), "GetAmmoItem") == null)
                throw new Exception("Humanoid.GetAmmoItem() not found - the equipped quiver could not be marked");
            if (AccessTools.Field(typeof(HotkeyBar), "m_elementPrefab") == null)
                throw new Exception("HotkeyBar.m_elementPrefab not found - there is no vanilla tile to clone");

            Harmony.Patch(hudUpdate, postfix: new HarmonyMethod(self, nameof(HudUpdatePostfix)));
            Harmony.Patch(invChanged, postfix: new HarmonyMethod(self, nameof(InventoryChangedPostfix)));
            Harmony.Patch(spawned, postfix: new HarmonyMethod(self, nameof(SpawnedPostfix)));

            Log.LogInfo("[AmmoHud] ready: ammo slots=" + SlotLayout.AmmoCount +
                        " offset=" + _offsetX.Value.ToString("0") + "," + _offsetY.Value.ToString("0") +
                        " scale=" + _scale.Value.ToString("0.##") +
                        " alpha=" + _alpha.Value.ToString("0.##") +
                        " (the 'built N tile(s)' line follows once the HUD lays out)");
        }

        private static void Require(MethodBase m, string what)
        {
            if (m == null) throw new Exception(what + " not found");
        }

        public override void Disable()
        {
            AmmoHudView.Destroy();
            base.Disable();
        }

        private static bool Live()
        {
            return _inst != null && _inst.Active && ClientActive();
        }

        // ---- patch bodies -----------------------------------------------------------------

        private static void HudUpdatePostfix(Hud __instance)
        {
            if (!Live()) return;
            try { AmmoHudView.Tick(__instance, Player.m_localPlayer); }
            catch (Exception e)
            {
                if (_tickErrors++ < 3)
                    Log.LogError("[AmmoHud] HUD tick failed (" + _tickErrors + "/3): " + e);
            }
        }

        private static void InventoryChangedPostfix(Player __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            AmmoHudView.Invalidate();
        }

        private static void SpawnedPostfix(Player __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            AmmoHudView.Invalidate();
        }

        // ---- status -----------------------------------------------------------------------

        public override string StatusDetail()
        {
            return "ammoSlots=" + SlotLayout.AmmoCount + "  " + AmmoHudView.Describe();
        }
    }
}
