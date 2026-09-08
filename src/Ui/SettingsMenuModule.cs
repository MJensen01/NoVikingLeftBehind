using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **SettingsMenu** - puts the NoVikingLeftBehind tab into Valheim's own Settings menu.
    ///
    /// Client-side only: a dedicated server has no <c>Settings</c> object and no canvas. The
    /// module owns nothing but the hook - all the UI lives in <see cref="NvlbSettingsTab"/>, and
    /// everything it can change goes through <see cref="TweakDoor"/>.
    ///
    /// The hook is a **prefix** on <c>Settings.Awake</c>, not a postfix, and that is deliberate:
    /// <c>Awake</c> calls <c>InitializeTabs()</c>, which snapshots the tab list into a private
    /// <c>SettingsTabs</c> list it later indexes by tab number, so a tab added afterwards would
    /// either be missed or index out of range. Added before, vanilla adopts our page like one of
    /// its own.
    /// </summary>
    internal sealed class SettingsMenuModule : FeatureModule
    {
        public override string Name => "SettingsMenu";
        public override string Section => "SettingsMenu";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Theme => "Server";
        public override string Hint => "Adds a settings tab to Valheim's own menu";

        protected override string EnabledDescription =>
            "Add a NoVikingLeftBehind tab to Valheim's Settings menu, so this mod's settings can " +
            "be seen and (where the server allows it) changed in game instead of by editing a " +
            "config file. Machine-local in effect: turning it off only hides the tab for you.";

        private static SettingsMenuModule _inst;
        private ConfigEntry<bool> _showUnavailable;

        private SettingsMenuModule() { _inst = this; }

        /// <summary>Should rows the player cannot change be listed (greyed) or hidden?</summary>
        public static bool ShowUnavailable
        {
            get { return _inst == null || _inst._showUnavailable == null || _inst._showUnavailable.Value; }
        }

        protected override void Bind()
        {
            _inst = this;
            _showUnavailable = BindLocal("ShowUnavailable", true,
                "Show settings you are not allowed to change, greyed out with the reason, rather " +
                "than hiding them. On by default so everyone can see what the mod can do. " +
                "Machine-local: this only affects your own menu.",
                Opt.B("Show settings you cannot change, greyed out"));
        }

        protected override void ApplyPatches()
        {
            var awake = AccessTools.Method(typeof(Settings), "Awake");
            if (awake == null)
                throw new Exception("Settings.Awake() not found - the settings menu changed shape");

            if (AccessTools.Field(typeof(TabHandler), "m_tabs") == null)
                throw new Exception("TabHandler.m_tabs not found");
            if (AccessTools.Method(typeof(TabHandler), "SetActiveTab",
                                   new[] { typeof(int), typeof(bool), typeof(bool) }) == null)
                throw new Exception("TabHandler.SetActiveTab(int,bool,bool) not found");

            Harmony.Patch(awake,
                prefix: new HarmonyMethod(typeof(SettingsMenuModule), nameof(SettingsAwakePrefix)));
        }

        private static void SettingsAwakePrefix(Settings __instance)
        {
            if (_inst == null || !_inst.Active) return;
            try { NvlbSettingsTab.Install(__instance); }
            catch (Exception e)
            {
                // Never let the tab stop the vanilla settings menu from opening.
                Log.LogError("[SettingsMenu] could not add the tab: " + e);
            }
        }

        public override string StatusDetail()
        {
            return "tab=\"" + NvlbSettingsTab.TabTitle + "\" showUnavailable=" + ShowUnavailable +
                   " settings=" + ConfigCatalog.All.Count;
        }
    }
}
