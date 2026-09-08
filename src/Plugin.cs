using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>How the plugin decides which half of itself to run.</summary>
    public enum RunMode
    {
        /// <summary>Dedicated server (-batchmode) = server half, anything else = client half.</summary>
        Auto,
        /// <summary>Force the server half.</summary>
        Server,
        /// <summary>Force the client half.</summary>
        Client
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class NoVikingLeftBehindPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Nosferatu.NoVikingLeftBehind";
        public const string PluginName = "NoVikingLeftBehind";
        public const string PluginVersion = "0.8.0";

        internal static ManualLogSource Log;
        internal static ConfigSync ConfigSync;
        internal static ConfigFile Cfg;

        internal static readonly List<FeatureModule> Modules = new List<FeatureModule>();

        internal static ConfigEntry<RunMode> ModeCfg;
        internal static ConfigEntry<bool> EnforceClientMod;
        internal static ConfigEntry<bool> HotReloadCfg;

        /// <summary>The half of the mod this process is running.</summary>
        internal static ModuleSide RunningSide = ModuleSide.Client;

        internal static bool IsServerSide => RunningSide == ModuleSide.Server;

        private Harmony _bootstrap;
        private static bool _summaryLogged;
        private ConfigWatcher _configWatcher;

        private void Awake()
        {
            Log = BepInEx.Logging.Logger.CreateLogSource("NVLB");
            Cfg = Config;

            ConfigSync = new ConfigSync(PluginGuid)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };

            ModeCfg = BindLocal("General", "Mode", RunMode.Auto,
                "Which half of the mod to run. Auto = dedicated server (-batchmode) runs the " +
                "server half, everything else runs the client half. Machine-local, never synced.",
                null, Opt.C("Which half of the mod this install runs").Admin().Restart());

            HotReloadCfg = BindLocal("General", "HotReload", true,
                "Watch this plugin's own cfg file on disk and reload it automatically when it " +
                "changes, so edits take effect without a server restart. Machine-local, never synced.",
                null, Opt.B("Apply config edits without a restart").Admin());

            EnforceClientMod = BindSynced("General", "EnforceClientMod", true,
                "Server: require every connecting client to run NoVikingLeftBehind " + PluginVersion +
                " or newer. Vanilla clients and clients with an older version are disconnected " +
                "with an explanatory message. Also locks the synced config so only the server " +
                "(and admins) can change it. Turn off to let vanilla clients join - the server " +
                "half still works, the client-side features simply do not exist for them.",
                null, Opt.B("Require every player to run this mod").Admin());
            ConfigSync.AddLockingConfigEntry(EnforceClientMod);
            EnforceClientMod.SettingChanged += (s, a) => ApplyEnforcement();
            ApplyEnforcement();

            Log.LogInfo("ServerSync initialised: id=" + ConfigSync.Name +
                        " display=" + ConfigSync.DisplayName +
                        " CurrentVersion=" + ConfigSync.CurrentVersion +
                        " MinimumRequiredVersion=" + ConfigSync.MinimumRequiredVersion +
                        " ModRequired=" + ConfigSync.ModRequired +
                        " EnforceClientMod=" + EnforceClientMod.Value);

            RunningSide = ResolveSide();
            Log.LogInfo(PluginName + " " + PluginVersion + ": mode=" + ModeCfg.Value +
                        " -> running the " + (IsServerSide ? "SERVER" : "CLIENT") + " half" +
                        " (isBatchMode=" + Application.isBatchMode + ")");

            // Frontier/Tiers are shared state every module may read at Bind() time, so they are
            // bound before the modules are discovered.
            Frontier.BindConfig();
            Tiers.BindConfig();

            DiscoverModules();

            foreach (var m in Modules)
            {
                try { m.Configure(Config); }
                catch (Exception e) { Log.LogError("[" + m.Name + "] config bind failed: " + e); }
            }

            // Patch now, not at ZNet.Start: ServerKeys hooks ZoneSystem.SetStartingGlobalKeys,
            // which runs INSIDE ZNet.Start -> ServerLoadWorld -> LoadWorld. Every patch body
            // still gates on its side at runtime.
            foreach (var m in Modules) m.TryEnable(PluginGuid, RunningSide);

            _bootstrap = new Harmony(PluginGuid + ".bootstrap");
            var znetStart = AccessTools.Method(typeof(ZNet), "Start");
            if (znetStart == null)
                Log.LogError(PluginName + ": ZNet.Start not found - cannot log the module summary");
            else
                _bootstrap.Patch(znetStart,
                    postfix: new HarmonyMethod(typeof(NoVikingLeftBehindPlugin), nameof(ZNetStartPostfix)));

            // Live config reload: watches Nosferatu.NoVikingLeftBehind.cfg on disk and calls
            // Config.Reload() (debounced, on the main thread via Update()) so edits on a
            // running server take effect without a restart. See ConfigWatcher.cs.
            _configWatcher = new ConfigWatcher(Cfg, Log, "[Config]");

            // Every setting has now declared itself through the two bind helpers, so the catalog
            // is complete. One line proves the metadata pass headlessly; the server also drops a
            // machine-readable copy next to the cfg for tooling (cfg.py, the settings tab's docs).
            Log.LogInfo(ConfigCatalog.SummaryLine());
            if (IsServerSide) WriteCatalogFile();

            Log.LogInfo(PluginName + " " + PluginVersion + " loaded, " + Modules.Count + " modules");
        }

        /// <summary>
        /// Dump the catalog beside the cfg file as TSV. Server-side only, best effort: it is a
        /// tooling convenience (and the headless proof of the metadata pass), never a dependency.
        /// The name deliberately does not end in .cfg or .bak-* so neither BepInEx nor cfg.py
        /// picks it up.
        /// </summary>
        private static void WriteCatalogFile()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Cfg.ConfigFilePath);
                if (string.IsNullOrEmpty(dir)) return;
                var path = System.IO.Path.Combine(dir, "nvlb-catalog.tsv");
                System.IO.File.WriteAllText(path, ConfigCatalog.Tsv());
                Log.LogInfo("ConfigCatalog: wrote " + path);
            }
            catch (Exception e)
            {
                Log.LogWarning("ConfigCatalog: could not write the catalog dump: " + e.Message);
            }
        }

        // ---- side resolution -------------------------------------------------------------

        private static ModuleSide ResolveSide()
        {
            switch (ModeCfg.Value)
            {
                case RunMode.Server: return ModuleSide.Server;
                case RunMode.Client: return ModuleSide.Client;
                default:
                    // ZNet.IsDedicated() is an instance method and ZNet does not exist yet at
                    // Awake, so the dedicated server is identified by Unity's headless flag.
                    // Verified in NOTES 11.1: the server process runs -nographics -batchmode.
                    return Application.isBatchMode ? ModuleSide.Server : ModuleSide.Client;
            }
        }

        private static void ApplyEnforcement()
        {
            bool on = EnforceClientMod != null && EnforceClientMod.Value;
            ConfigSync.ModRequired = on;
            ConfigSync.MinimumRequiredVersion = on ? PluginVersion : "0.0.0";
        }

        // ---- module auto-discovery ---------------------------------------------------------

        private static void DiscoverModules()
        {
            var found = new List<FeatureModule>();
            Type[] types;
            try { types = Assembly.GetExecutingAssembly().GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.IsAbstract || !typeof(FeatureModule).IsAssignableFrom(t)) continue;
                try
                {
                    found.Add((FeatureModule)Activator.CreateInstance(t, true));
                }
                catch (Exception e)
                {
                    Log.LogError("module discovery: cannot instantiate " + t.Name + ": " + e);
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            Modules.Clear();
            Modules.AddRange(found);
        }

        // ---- config helpers ----------------------------------------------------------------

        internal static ConfigEntry<T> BindSynced<T>(string section, string key, T defaultValue,
                                                     string description, FeatureModule owner,
                                                     Opt opt = null)
        {
            var entry = Cfg.Bind(section, key, defaultValue, description);
            ConfigSync.AddConfigEntry(entry).SynchronizedConfig = true;
            Wire(entry, owner);
            ConfigCatalog.Register(entry, opt, owner, false);
            return entry;
        }

        internal static ConfigEntry<T> BindLocal<T>(string section, string key, T defaultValue,
                                                    string description, FeatureModule owner,
                                                    Opt opt = null)
        {
            // Deliberately NOT registered with ConfigSync: a local entry stays editable on the
            // client even when the synced config is locked.
            var entry = Cfg.Bind(section, key, defaultValue, description);
            Wire(entry, owner);
            ConfigCatalog.Register(entry, opt, owner, true);
            return entry;
        }

        private static void Wire<T>(ConfigEntry<T> entry, FeatureModule owner)
        {
            entry.SettingChanged += (s, a) =>
            {
                try
                {
                    if (owner != null) owner.OnConfigChanged(entry);
                    else ConfigChanged?.Invoke(entry);
                }
                catch (Exception e)
                {
                    Log.LogError("OnConfigChanged(" + entry.Definition + ") threw: " + e);
                }
            };
        }

        /// <summary>Fires for plugin-level (owner-less) synced entries, e.g. [Frontier].</summary>
        internal static event Action<ConfigEntryBase> ConfigChanged;

        // ---- summary ------------------------------------------------------------------------

        internal static string ModuleSummary()
        {
            var parts = new List<string>();
            foreach (var m in Modules) parts.Add(m.Name + "=" + m.Status);
            return PluginName + " module summary: " + string.Join(", ", parts.ToArray());
        }

        internal static IEnumerable<string> StatusLines()
        {
            var lines = new List<string>();
            lines.Add(PluginName + " " + PluginVersion + " (" + (IsServerSide ? "server" : "client") + " half)");
            lines.Add("  WorldTier=" + Frontier.Describe() + "  TiersBehind=" + Frontier.TiersBehind.Value +
                      "  behind-the-frontier tiers: " + Frontier.BehindRangeText());
            lines.Add("  EnforceClientMod=" + EnforceClientMod.Value +
                      "  configLocked=" + ConfigSync.IsLocked +
                      "  sourceOfTruth=" + ConfigSync.IsSourceOfTruth);
            foreach (var m in Modules)
            {
                var detail = m.StatusDetail();
                lines.Add("  " + m.Name + " [" + m.Side + "] = " + m.Status +
                          (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
            }
            return lines;
        }

        private static void ZNetStartPostfix()
        {
            if (_summaryLogged) return;
            _summaryLogged = true;

            // Sanity-check Auto against the real answer now that ZNet exists.
            if (ZNet.instance != null && ModeCfg.Value == RunMode.Auto)
            {
                bool dedicated = ZNet.instance.IsDedicated();
                if (dedicated != IsServerSide)
                    Log.LogWarning(PluginName + ": Auto mode picked the " +
                                   (IsServerSide ? "server" : "client") + " half but ZNet.IsDedicated()=" +
                                   dedicated + ". Set [General] Mode explicitly.");
            }

            Log.LogInfo("ServerSync: patches installed=" +
                        Harmony.HasAnyPatches("org.bepinex.helpers.ServerSync") +
                        ", initialSyncDone=" + ConfigSync.InitialSyncDone);
            Log.LogInfo(ModuleSummary());
            foreach (var line in StatusLines()) Log.LogInfo(line);
        }

        private void Update()
        {
            if (HotReloadCfg != null && HotReloadCfg.Value) _configWatcher?.Pump();
            AccessModule.Pump();
        }

        private void OnDestroy()
        {
            foreach (var m in Modules) m.Disable();
            _configWatcher?.Dispose();
            try { if (_bootstrap != null) _bootstrap.UnpatchSelf(); }
            catch { /* shutting down */ }
        }
    }
}
