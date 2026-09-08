using System;
using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **SettingsMenu** - puts the NoVikingLeftBehind tab into Valheim's own Settings menu.
    ///
    /// Client-side only: a dedicated server has no <c>Settings</c> object and no canvas. The
    /// module owns nothing but the hooks - all the UI lives in <see cref="NvlbSettingsTab"/>, and
    /// everything it can change goes through <see cref="TweakDoor"/>.
    ///
    /// Where it hooks, and why more than once
    /// --------------------------------------
    /// The natural seam is a **prefix** on <c>Settings.Awake</c>: <c>Awake</c> calls
    /// <c>InitializeTabs()</c>, which snapshots the tab list into a private <c>SettingsTabs</c>
    /// list it later indexes by tab number, so a tab added before that runs is adopted like one
    /// of vanilla's own - <c>Initialize</c>, <c>OnTabOpen</c>, <c>OnOk</c>, <c>OnBack</c>,
    /// <c>Terminate</c> all arrive for free.
    ///
    /// In 0.7.1 that prefix did not put a tab on the screen for one tester and left no trace in
    /// the log either way, so 0.7.2 stops relying on a single seam. Four hooks now call the same
    /// idempotent <see cref="NvlbSettingsTab.Install"/>: the <c>Settings.Awake</c> prefix, a
    /// <c>Settings.Awake</c> **postfix**, a <c>TabHandler.Init</c> prefix, and postfixes on the
    /// two places that instantiate the settings prefab (<c>Menu.OnSettings</c> in game,
    /// <c>FejdStartup.OnButtonSettings</c> on the main menu). Whichever fires first wins; the
    /// rest find the <c>NVLB_Page</c> already there and do nothing.
    ///
    /// A hook that arrives **after** <c>InitializeTabs</c> has run has one extra job, which
    /// <see cref="Ensure"/> does: vanilla's <c>SettingsTabs</c> snapshot has to be rebuilt (its
    /// private <c>SetAvailableTabs()</c>, by reflection) so the list still lines up with the tab
    /// bar index for index, and our page has to be told to <c>Initialize()</c> itself because
    /// vanilla's loop over that list has already been and gone.
    ///
    /// Everything here logs. A tab that quietly does not appear is not a bug anyone can chase
    /// from a screenshot, so every hook says it fired and every refusal says why.
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

        // Vanilla's private tab bookkeeping, for the hooks that arrive late.
        private static FieldInfo _fSettingsTabs;
        private static MethodInfo _mSetAvailableTabs;
        private static MethodBase _awake;
        private static bool _reportedNeighbours;
        private static bool _uiDumpRegistered;

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

            _fSettingsTabs = AccessTools.Field(typeof(Settings), "SettingsTabs");
            _mSetAvailableTabs = AccessTools.Method(typeof(Settings), "SetAvailableTabs");
            if (_fSettingsTabs == null || _mSetAvailableTabs == null)
                Log.LogWarning("[SettingsMenu] Settings.SettingsTabs / SetAvailableTabs not found - " +
                               "a late hook could add the tab button but vanilla would not drive the page");

            // Say exactly what Harmony is about to rewrite. If the tab never appears, this line
            // is the first thing to check: a zero-byte body, or a declaring type that is not the
            // one the game instantiates, means the seam is wrong rather than the UI code.
            Log.LogInfo("[SettingsMenu] patching " + Describe(awake));
            _awake = awake;

            Harmony.Patch(awake,
                prefix: new HarmonyMethod(typeof(SettingsMenuModule), nameof(SettingsAwakePrefix)),
                postfix: new HarmonyMethod(typeof(SettingsMenuModule), nameof(SettingsAwakePostfix)));

            // ---- belt and braces ---------------------------------------------------------------
            // None of these is expected to be the one that works; each is here so that the tab
            // still exists if Settings.Awake turns out not to be the seam on someone's build.
            PatchQuietly(AccessTools.Method(typeof(TabHandler), "Init", new[] { typeof(bool) }),
                         nameof(TabHandlerInitPrefix), true, "TabHandler.Init(bool)");
            PatchQuietly(AccessTools.Method(typeof(Menu), "OnSettings"),
                         nameof(MenuOnSettingsPostfix), false, "Menu.OnSettings");
            PatchQuietly(AccessTools.Method(typeof(FejdStartup), "OnButtonSettings"),
                         nameof(FejdOnButtonSettingsPostfix), false, "FejdStartup.OnButtonSettings");

            PatchQuietly(AccessTools.Method(typeof(Terminal), "InitTerminal"),
                         nameof(RegisterUiDump), false, "Terminal.InitTerminal");
        }

        /// <summary>
        /// <c>nvlb.uidump</c> - what a screenshot cannot tell you: for every label on the built
        /// page, whether it is active, enabled, how big its font is against how big its rect is,
        /// its alpha, where it actually landed on screen, and whether an ancestor CanvasGroup is
        /// fading it. A label that does not draw is nearly always one of those.
        /// </summary>
        private static void RegisterUiDump()
        {
            if (_uiDumpRegistered) return;
            _uiDumpRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.uidump",
                    "nvlb.uidump - dump every label on the NoVikingLeftBehind settings page with " +
                    "its size, rect, alpha and screen position. Open the tab first.",
                    new Terminal.ConsoleEvent(RunUiDump));
                Log.LogInfo("[SettingsMenu] console command 'nvlb.uidump' registered");
            }
            catch (Exception e)
            {
                _uiDumpRegistered = false;
                Log.LogError("[SettingsMenu] could not register nvlb.uidump: " + e);
            }
        }

        private static void RunUiDump(Terminal.ConsoleEventArgs args)
        {
            NvlbSettingsTab.Dump(delegate (string line)
            {
                Log.LogInfo(line);
                if (args != null && args.Context != null) args.Context.AddString(line);
            });
        }

        /// <summary>A secondary hook: worth a warning if it is missing, never worth failing the module.</summary>
        private void PatchQuietly(MethodBase target, string handler, bool asPrefix, string label)
        {
            if (target == null)
            {
                Log.LogWarning("[SettingsMenu] backup hook " + label + " not found - skipped");
                return;
            }
            try
            {
                var hm = new HarmonyMethod(typeof(SettingsMenuModule), handler);
                Harmony.Patch(target, prefix: asPrefix ? hm : null, postfix: asPrefix ? null : hm);
                Log.LogInfo("[SettingsMenu] backup hook on " + Describe(target));
            }
            catch (Exception e)
            {
                Log.LogWarning("[SettingsMenu] backup hook " + label + " failed: " + e.Message);
            }
        }

        private static string Describe(MethodBase m)
        {
            if (m == null) return "<null>";
            int il = -1;
            try
            {
                var body = m.GetMethodBody();
                if (body != null)
                {
                    var bytes = body.GetILAsByteArray();
                    if (bytes != null) il = bytes.Length;
                }
            }
            catch { /* diagnostics must never throw */ }

            var t = m.DeclaringType;
            return (t == null ? "?" : t.FullName) + "::" + m.Name +
                   " [" + (t == null ? "?" : t.Assembly.GetName().Name) + "]" +
                   " il=" + il + "B" + (m.IsStatic ? " static" : "") + (m.IsPrivate ? " private" : "");
        }

        private static string Describe(Settings s)
        {
            if (s == null) return "null";
            var h = s.GetComponentInChildren<TabHandler>(true);
            return "'" + s.gameObject.name + "' tabs=" +
                   (h == null ? "no TabHandler" : (h.m_tabs == null ? "null" : h.m_tabs.Count.ToString()));
        }

        // ---- the hooks ------------------------------------------------------------------------------

        private static void SettingsAwakePrefix(Settings __instance)
        {
            Log.LogInfo("[SettingsMenu] Settings.Awake prefix fired (instance=" + Describe(__instance) + ")");
            Ensure(__instance, "Settings.Awake prefix");
        }

        private static void SettingsAwakePostfix(Settings __instance)
        {
            Ensure(__instance, "Settings.Awake postfix");
        }

        private static void TabHandlerInitPrefix(TabHandler __instance)
        {
            if (__instance == null) return;
            var settings = __instance.GetComponentInParent<Settings>();
            if (settings == null) return;   // the server-list tab bar, not the settings menu
            Log.LogInfo("[SettingsMenu] TabHandler.Init prefix fired on the settings menu");
            Ensure(settings, "TabHandler.Init prefix");
        }

        private static void MenuOnSettingsPostfix()
        {
            Ensure(Settings.instance, "Menu.OnSettings postfix");
        }

        private static void FejdOnButtonSettingsPostfix()
        {
            Ensure(Settings.instance, "FejdStartup.OnButtonSettings postfix");
        }

        /// <summary>
        /// Once, the first time a hook fires: who else has patched <c>Settings.Awake</c>. Harmony
        /// skips the remaining prefixes once one of them returns false, so another mod's prefix is
        /// one of the few things that can make ours never run - and by the time the menu is opened
        /// every mod has loaded, which is why this is asked here and not at patch time.
        /// </summary>
        private static void ReportNeighbours()
        {
            if (_reportedNeighbours || _awake == null) return;
            _reportedNeighbours = true;
            try
            {
                var info = HarmonyLib.Harmony.GetPatchInfo(_awake);
                if (info == null) { Log.LogWarning("[SettingsMenu] Settings.Awake reports no patches at all"); return; }

                var owners = "";
                foreach (var o in info.Owners) owners += (owners.Length > 0 ? ", " : "") + o;
                Log.LogInfo("[SettingsMenu] Settings.Awake patches: prefixes=" + info.Prefixes.Count +
                            " postfixes=" + info.Postfixes.Count + " transpilers=" + info.Transpilers.Count +
                            " owners=[" + owners + "]");
            }
            catch (Exception e) { Log.LogWarning("[SettingsMenu] could not read the patch list: " + e.Message); }
        }

        // ---- the one place that installs ------------------------------------------------------------

        /// <summary>
        /// Add the tab if it is missing, and - when vanilla has already snapshotted its tab list -
        /// re-sync that snapshot and initialise our page by hand. Safe to call any number of times.
        /// </summary>
        private static void Ensure(Settings settings, string via)
        {
            if (_inst == null || !_inst.Active)
            {
                // Only the first hook says so, or turning the tab off would print this four
                // times every time the settings menu is opened.
                if (via.StartsWith("Settings.Awake prefix"))
                    Log.LogInfo("[SettingsMenu] " + via + ": the module is not active (module=" +
                                (_inst != null) + " applied=" + (_inst != null && _inst.Applied) +
                                " enabled=" + (_inst != null && _inst.Enabled) + ") - no tab");
                return;
            }

            ReportNeighbours();

            try
            {
                if (settings == null) settings = Settings.instance;
                if (settings == null)
                {
                    Log.LogWarning("[SettingsMenu] " + via + ": there is no Settings object to add a tab to");
                    return;
                }

                // Null until InitializeTabs has run; non-null means we are late to the party.
                bool alreadySnapshotted = _fSettingsTabs != null && _fSettingsTabs.GetValue(settings) != null;

                var tab = NvlbSettingsTab.Install(settings, via);
                if (tab == null) return;   // already there, or Install has said why not

                if (!alreadySnapshotted) return;   // vanilla's own loop will pick our page up

                if (_mSetAvailableTabs == null)
                {
                    Log.LogWarning("[SettingsMenu] " + via + ": added the tab late but cannot re-sync vanilla's " +
                                   "SettingsTabs list - the button will be there, the page may not work");
                    return;
                }

                _mSetAvailableTabs.Invoke(settings, null);
                var list = _fSettingsTabs != null ? _fSettingsTabs.GetValue(settings) as ICollection : null;
                Log.LogInfo("[SettingsMenu] " + via + ": vanilla had already snapshotted its tabs - " +
                            "re-synced SettingsTabs (" + (list == null ? -1 : list.Count) + " entries)");
                tab.Initialize();
            }
            catch (Exception e)
            {
                // Never let the tab stop the vanilla settings menu from opening.
                Log.LogError("[SettingsMenu] could not add the tab (" + via + "): " + e);
            }
        }

        public override string StatusDetail()
        {
            return "tab=\"" + NvlbSettingsTab.TabTitle + "\" showUnavailable=" + ShowUnavailable +
                   " settings=" + ConfigCatalog.All.Count;
        }
    }
}
