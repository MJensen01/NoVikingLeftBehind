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
    /// can be switched off server-wide for everyone; the offset, scale, opacity and background-opacity
    /// knobs are <c>BindLocal</c>, so each player positions and fades it to taste from the in-game
    /// settings tab with no rebuild and no restart.
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
        private ConfigEntry<float> _bgAlpha;

        /// <summary>Alpha's default in 0.10.0 - the one value that is moved to the new default.</summary>
        private const float OldAlphaDefault = 0.9f;

        /// <summary>Scale's default in 0.10.0 - the one value that is moved to the new default.</summary>
        private const float OldScaleDefault = 1f;

        private static bool _alphaMigrationLogged;
        private static bool _scaleMigrationLogged;

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
            _scale = BindLocal("Scale", 0.8f,
                "Machine-local. Size of the ammo readout relative to the hotbar (0.4-2.5). 1 draws " +
                "the tiles exactly the size of a hotbar slot; the default 0.8 makes them a little " +
                "smaller than the hotbar, which is what keeps a passive readout out of the way. " +
                "(Was 1 up to 0.10.0.)",
                Opt.N("Size of the ammo readout, 1 matches the hotbar", 0.4, 2.5, 0.05));
            _alpha = BindLocal("Alpha", 0.65f,
                "Machine-local. Opacity of the WHOLE readout, icon and count included (0.1-1). The " +
                "default sits it well behind the hotbar so it reads as background information; the " +
                "quiver you have equipped is drawn at full strength within that and the others at " +
                "0.7 of it. (Was 0.9 up to 0.10.0.)",
                Opt.N("Opacity of the ammo readout", 0.1, 1.0, 0.05));
            _bgAlpha = BindLocal("BackgroundAlpha", 0.45f,
                "Machine-local. Opacity of the tile BACKGROUND only (0-1), inside the readout's own " +
                "Alpha - so the dark square can be faded right back while the icon and the count " +
                "stay readable on top of it. 0 draws no square at all, just the icon and the number; " +
                "1 is a solid black tile. New in 0.10.1: before it, the copied hotbar slot lost " +
                "Valheim's own button tint and drew as a bright, near-opaque grey block.",
                Opt.N("Opacity of the ammo tile's dark background", 0.0, 1.0, 0.05));

            MigrateAlphaDefault();
            MigrateScaleDefault();

            _inst = this;
            Push();
        }

        /// <summary>
        /// 0.10.0 shipped Alpha=0.9 and Scale=1 against a tile that was accidentally drawn at full
        /// brightness (see <see cref="AmmoHudView"/>), which made the readout a pair of bright grey
        /// blocks. 0.10.1 fixes the tile and lowers both defaults to suit it. A cfg still holding
        /// exactly the old default is moved to the new one; anything a player set themselves -
        /// including a deliberate 0.9 typed after this release - is left alone, because "identical
        /// to the old default" is the only thing we can tell apart. Same convention as
        /// <c>LoadoutsModule.MigrateLoadout2Key</c>.
        /// </summary>
        private void MigrateAlphaDefault()
        {
            if (_alpha == null || !Mathf.Approximately(_alpha.Value, OldAlphaDefault)) return;
            _alpha.Value = (float)_alpha.DefaultValue;
            if (_alphaMigrationLogged) return;
            _alphaMigrationLogged = true;
            Log.LogWarning("[AmmoHud] Alpha was still the old default " + OldAlphaDefault +
                           " - moved to " + _alpha.Value + " (0.10.1 tones the readout down; set it " +
                           "back in [AmmoHud] Alpha if you want the old brightness).");
        }

        private void MigrateScaleDefault()
        {
            if (_scale == null || !Mathf.Approximately(_scale.Value, OldScaleDefault)) return;
            _scale.Value = (float)_scale.DefaultValue;
            if (_scaleMigrationLogged) return;
            _scaleMigrationLogged = true;
            Log.LogWarning("[AmmoHud] Scale was still the old default " + OldScaleDefault +
                           " - moved to " + _scale.Value + " (0.10.1 shrinks the readout; set it " +
                           "back in [AmmoHud] Scale if you want hotbar-sized tiles).");
        }

        private void Push()
        {
            AmmoHudView.Configure(new Vector2(_offsetX.Value, _offsetY.Value),
                                  _scale.Value, _alpha.Value, _bgAlpha.Value);
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
                        " backgroundAlpha=" + _bgAlpha.Value.ToString("0.##") +
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
