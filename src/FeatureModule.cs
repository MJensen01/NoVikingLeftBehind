using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>Which half of the mod a module belongs to.</summary>
    internal enum ModuleSide
    {
        /// <summary>Dedicated server only (never patched on a player's client).</summary>
        Server,
        /// <summary>Player client only (never patched on a dedicated server).</summary>
        Client,
        /// <summary>Both halves.</summary>
        Both
    }

    /// <summary>
    /// One toggleable feature. Owns its own Harmony instance so a failure in one module
    /// can never take down the others or the plugin.
    ///
    /// To add a module: drop a new file in this project with a non-abstract subclass of
    /// FeatureModule. The plugin reflects over the assembly at Awake and instantiates every
    /// one it finds (public parameterless ctor, or none at all) - there is no registry to edit.
    ///
    /// Contract:
    ///   override string Name        - short identifier, used in the log prefix, the module
    ///                                 summary, nvlb.status and the Harmony instance id.
    ///   override ModuleSide Side    - Server / Client / Both. A module whose side does not
    ///                                 match the running half is never configured or patched.
    ///   override void Bind()        - bind config entries with BindSynced/BindLocal.
    ///                                 [Section] "Enabled" is bound for you before Bind() runs.
    ///   override void ApplyPatches()- install Harmony patches on `Harmony`. THROW loudly on
    ///                                 any mismatch (missing method, transpiler match count) -
    ///                                 the throw becomes FAILED(reason) in the module summary
    ///                                 and disables only this module.
    /// Optional:
    ///   override string Section     - config section name (defaults to Name).
    ///   override bool DefaultEnabled- default of [Section] Enabled (defaults to true).
    ///   override void OnConfigChanged(ConfigEntryBase) - hot reload; fires for every entry
    ///                                 bound through BindSynced/BindLocal, including when the
    ///                                 server pushes a new value over ServerSync.
    ///   override string StatusDetail() - one line of detail for nvlb.status.
    ///
    /// Every patch body must gate on `Active` (applied AND still enabled) plus the side gate
    /// (ServerActive() / ClientActive()) so the same DLL behaves like vanilla on the other half.
    /// </summary>
    internal abstract class FeatureModule
    {
        public abstract string Name { get; }

        public virtual ModuleSide Side => ModuleSide.Both;
        public virtual string Section => Name;
        public virtual bool DefaultEnabled => true;
        protected virtual string EnabledDescription => "Enable the " + Name + " module.";

        /// <summary>
        /// Which group the in-game settings tab lists this module under. One of:
        /// "Catching up", "Inventory", "Building &amp; gathering", "Food &amp; survival",
        /// "On the water", "Death", "Combat &amp; powers", "World", "Server".
        /// </summary>
        public virtual string Theme => "Server";

        /// <summary>One plain line, &lt;= 12 words, shown next to the module in the settings tab.</summary>
        public virtual string Hint => null;

        /// <summary>
        /// Metadata for this module's own [Section] Enabled toggle. Override to make it
        /// admin-only (<c>base.EnabledOpt.Admin()</c>) - the ON direction's restart rule is
        /// handled by the tweak door, not by <see cref="Opt.Restart"/>.
        /// </summary>
        protected virtual Opt EnabledOpt
        {
            get { return Opt.B(Hint ?? ("Turn the " + Name + " feature on or off")).As("Enabled"); }
        }

        public ConfigEntry<bool> EnabledCfg;
        protected Harmony Harmony;
        protected ConfigFile Cfg;

        public string Status = "not-run";
        public bool Applied;

        /// <summary>
        /// Was [Section] Enabled true when this process started? Harmony patches are installed
        /// exactly once, at plugin Awake, so a module that booted OFF cannot be switched ON
        /// without a restart - and one that booted ON can be toggled freely, because every patch
        /// body gates on <see cref="Active"/>. The tweak door uses this to decide which way.
        /// </summary>
        public bool BootEnabled;

        /// <summary>Config says on. Independent of whether the patches installed.</summary>
        public bool Enabled => EnabledCfg != null && EnabledCfg.Value;

        /// <summary>Patches installed AND config still on. Patch bodies gate on this.</summary>
        public bool Active => Applied && Enabled;

        protected static ManualLogSource Log => NoVikingLeftBehindPlugin.Log;

        // ---- lifecycle (called by the plugin, not by modules) --------------------------

        public void Configure(ConfigFile cfg)
        {
            Cfg = cfg;
            EnabledCfg = NoVikingLeftBehindPlugin.BindSynced(Section, "Enabled", DefaultEnabled,
                                                            EnabledDescription, this, EnabledOpt);
            Bind();
        }

        public void TryEnable(string guidPrefix, ModuleSide runningSide)
        {
            BootEnabled = Enabled;

            if (Side != ModuleSide.Both && Side != runningSide)
            {
                Applied = false;
                Status = "disabled(side)";
                return;
            }

            if (!Enabled)
            {
                Applied = false;
                Status = "disabled";
                return;
            }

            try
            {
                Harmony = new Harmony(guidPrefix + "." + Name);
                ApplyPatches();
                Applied = true;
                Status = "applied";
                Log.LogInfo("[" + Name + "] applied");
            }
            catch (Exception e)
            {
                Applied = false;
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                Status = "FAILED(" + msg + ")";
                Log.LogError("[" + Name + "] FAILED to patch: " + e);
                Disable();
            }
        }

        public virtual void Disable()
        {
            Applied = false;
            try
            {
                if (Harmony != null) Harmony.UnpatchSelf();
            }
            catch (Exception e)
            {
                Log.LogWarning("[" + Name + "] unpatch failed: " + e.Message);
            }
        }

        // ---- to implement -------------------------------------------------------------

        /// <summary>Bind this module's config entries. [Section] Enabled is already bound.</summary>
        protected abstract void Bind();

        /// <summary>Install the module's patches. Throw loudly on any mismatch.</summary>
        protected abstract void ApplyPatches();

        /// <summary>Hot reload: one of this module's config entries changed (local edit or server push).</summary>
        public virtual void OnConfigChanged(ConfigEntryBase entry) { }

        /// <summary>Extra detail for nvlb.status. Return null for none.</summary>
        public virtual string StatusDetail() { return null; }

        /// <summary>
        /// Last word on a free-text value a player typed into the in-game settings tab, checked
        /// on the SERVER before anything is written. Return null to accept, or a short sentence
        /// explaining the refusal. The default accepts anything that is not obviously junk;
        /// modules whose strings are prefab lists, key names or colour hex should override.
        /// </summary>
        public virtual string ValidateValue(SettingInfo info, string raw)
        {
            return null;
        }

        // ---- config helpers -----------------------------------------------------------

        /// <summary>Bind a server-synced entry: the server's value wins on every client.</summary>
        protected ConfigEntry<T> BindSynced<T>(string section, string key, T defaultValue,
                                               string description, Opt opt = null)
        {
            return NoVikingLeftBehindPlugin.BindSynced(section, key, defaultValue, description, this, opt);
        }

        /// <summary>Bind a server-synced entry in this module's own [Section].</summary>
        protected ConfigEntry<T> BindSynced<T>(string key, T defaultValue, string description, Opt opt = null)
        {
            return NoVikingLeftBehindPlugin.BindSynced(Section, key, defaultValue, description, this, opt);
        }

        /// <summary>Bind a machine-local entry: never synced, each install keeps its own value.</summary>
        protected ConfigEntry<T> BindLocal<T>(string section, string key, T defaultValue,
                                              string description, Opt opt = null)
        {
            return NoVikingLeftBehindPlugin.BindLocal(section, key, defaultValue, description, this, opt);
        }

        /// <summary>Bind a machine-local entry in this module's own [Section].</summary>
        protected ConfigEntry<T> BindLocal<T>(string key, T defaultValue, string description, Opt opt = null)
        {
            return NoVikingLeftBehindPlugin.BindLocal(Section, key, defaultValue, description, this, opt);
        }

        // ---- runtime gates --------------------------------------------------------------

        /// <summary>Runtime gate for server-side behaviour.</summary>
        protected internal static bool ServerActive()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>Runtime gate for client-side behaviour (a player's game, incl. a host).</summary>
        protected internal static bool ClientActive()
        {
            return ZNet.instance != null && !ZNet.instance.IsDedicated();
        }
    }
}
