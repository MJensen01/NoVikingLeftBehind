using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Server-authoritative world modifiers, driven from the plugin config instead of the
    /// world's stored server-option keys.
    ///
    /// Hook: Postfix on ZoneSystem.SetStartingGlobalKeys(bool). Vanilla calls it at the end of
    /// ZNet.LoadWorld (ZNet.decompiled.cs:1777) after ZoneSystem.Load has replayed the world's
    /// saved keys, so this is the first safe point where the key set is complete.
    ///
    /// We call the private GlobalKeyAdd/GlobalKeyRemove directly rather than the public
    /// SetGlobalKey/RemoveGlobalKey, because those two only route a ZRoutedRpc and the RPC
    /// path is not usable this early in world load.
    ///
    /// Scalars: Game.UpdateWorldRates divides the stored value by 100 (Game.cs:1340-1400,
    /// trySetScalarKey, multiplier 100f), so a 2.5x multiplier is stored as "skillgainrate 250".
    ///
    /// ---- 0.10.3: this module no longer fights the world ----------------------------------
    ///
    /// Keys persist into the world file (GlobalKeyAdd writes ZNet.World.m_startingGlobalKeys),
    /// and until 0.10.2 a *false* bool actively REMOVED its key on every world load. That
    /// deleted world keys the server operator had set with a vanilla launch flag
    /// (`-modifier deathpenalty casual` parses in FejdStartup into m_startingGlobalKeys and is
    /// replayed by SetStartingGlobalKeys, so our postfix ran right after it and undid it) —
    /// GitHub issue #8. A setting left at its default now means "NOT MANAGED BY NVLB":
    ///
    ///   * a false bool writes nothing and removes nothing;
    ///   * a scalar at its config default writes nothing and removes nothing;
    ///   * [ServerKeys] RemoveKeys stays the one explicit deletion tool.
    ///
    /// Achievements (GitHub issue #4): Achievements.cs -> ServerOptionsGUI.WorldContainsCheatedModifiers
    /// flags a world as cheated when ANY entry of World.m_startingGlobalKeys is not a key the
    /// vanilla World Modifiers GUI can produce — and there is no skill modifier in the GUI at
    /// all (enum WorldModifiers = Default, Combat, DeathPenalty, Resources, Raids, Portals).
    /// So "skillgainrate 250" written as a world key disables achievements for everyone on the
    /// server. [ServerKeys] RatesWithoutWorldKeys (default ON) applies the two skill rates
    /// straight into the statics the game actually reads (Game.m_skillGainRate /
    /// Game.m_skillReductionRate, Game.cs:212-214) with a postfix on Game.UpdateWorldRates,
    /// and writes no key at all. Every path that re-evaluates the rates — ZoneSystem.Start,
    /// GlobalKeyAdd, GlobalKeyRemove, ZoneSystem.Reset, ServerOptionsGUI, Player respawn —
    /// funnels through that one method, so the postfix is what keeps the rate from snapping
    /// back to 1.0 the moment anything touches a global key.
    ///
    /// Sides: the world-key half is dedicated-server only (as before — a listen-server host
    /// never wrote keys and still does not). The rate postfix must run on clients too, because
    /// Skills reads Game.m_skillGainRate locally (Skills.cs:68) — hence ModuleSide.Both with
    /// the key half gated on NoVikingLeftBehindPlugin.IsServerSide. The config is already
    /// server-synced, so a client applies the server's numbers.
    ///
    /// Hot reload: once a world is loaded (ZoneSystem.instance != null), a live config edit
    /// re-runs Apply() on the server immediately instead of waiting for the next world load;
    /// on a client (and on the server too) it re-runs UpdateWorldRates so the postfix reloads
    /// the statics from the new value.
    /// </summary>
    internal sealed class ServerKeysModule : FeatureModule
    {
        public override string Name => "ServerKeys";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "ServerKeys";
        public override string Theme => "World";
        public override string Hint => "Force world-wide skill rates, build costs, and death penalties";

        protected override Opt EnabledOpt => base.EnabledOpt.Admin();

        protected override string EnabledDescription =>
            "Apply the world modifiers below on world load. A setting left at its default is " +
            "left alone - the world's own keys (including vanilla -modifier launch flags) win.";

        private ConfigEntry<bool> _applyOnLoad;
        private ConfigEntry<bool> _ratesWithoutWorldKeys;
        private ConfigEntry<float> _skillGainRate;
        private ConfigEntry<float> _skillReductionRate;
        private ConfigEntry<bool> _noBuildCost;
        private ConfigEntry<bool> _noCraftCost;
        private ConfigEntry<bool> _allPiecesUnlocked;
        private ConfigEntry<bool> _noWorkbench;
        private ConfigEntry<bool> _allRecipesUnlocked;
        private ConfigEntry<bool> _deathKeepEquip;
        private ConfigEntry<string> _removeKeys;

        private static ServerKeysModule _self;

        protected override void Bind()
        {
            // These drive server-side world state only; a client never acts on them, but they
            // are synced so an admin sees the real values in the in-game config manager - and
            // since 0.10.3 the client half of RatesWithoutWorldKeys reads the two rates.
            _applyOnLoad = BindSynced("ApplyOnLoad", true,
                "Apply on every world load. Turn off to leave the world's own keys alone.",
                Opt.B("Apply these settings automatically every time the world loads").Admin());

            _ratesWithoutWorldKeys = BindSynced("RatesWithoutWorldKeys", true,
                "Apply SkillGainRate/SkillReductionRate WITHOUT writing a world key (recommended). " +
                "Valheim has no skill modifier in its World Modifiers menu, so a stored " +
                "'skillgainrate' key makes the game treat the world as cheated and turns achievements " +
                "off for everyone on the server. With this on the rates are applied directly to the " +
                "values the game reads, the key is never written, and a stray key an older version " +
                "left behind is removed once. Turn it off to go back to storing the rates in the world.",
                Opt.B("Apply skill rates without flagging the world as cheated").Admin());

            _skillGainRate = BindSynced("SkillGainRate", 1.0f,
                "Skill gain multiplier. 1.0 = vanilla, 2.5 = 2.5x. Left at its default, " +
                "NoVikingLeftBehind does not touch the rate at all - whatever the world or a vanilla " +
                "launch flag set stays. Stored in the world as value*100 only when " +
                "RatesWithoutWorldKeys is off.",
                Opt.N("How fast players gain skill levels", 0, 10).Admin());
            _skillReductionRate = BindSynced("SkillReductionRate", 1.0f,
                "Skill loss on death multiplier. 1.0 = vanilla, 0 = no skill loss. Left at its " +
                "default, NoVikingLeftBehind does not touch the rate at all - a vanilla " +
                "'-modifier deathpenalty casual' server keeps its own setting.",
                Opt.N("How much skill is lost when a player dies", 0, 5).Admin());

            _noBuildCost = BindSynced("NoBuildCost", false, "Building costs no resources. Off = not managed (the world's own key stands).",
                Opt.B("Building structures costs no resources").Admin());
            _noCraftCost = BindSynced("NoCraftCost", false, "Crafting costs no resources. Off = not managed (the world's own key stands).",
                Opt.B("Crafting items costs no resources").Admin());
            _allPiecesUnlocked = BindSynced("AllPiecesUnlocked", false, "All build pieces available. Off = not managed (the world's own key stands).",
                Opt.B("Every building piece is unlocked from the start").Admin());
            _noWorkbench = BindSynced("NoWorkbench", false, "No crafting-station requirement for building. Off = not managed (the world's own key stands).",
                Opt.B("Building doesn't require a nearby crafting station").Admin());
            _allRecipesUnlocked = BindSynced("AllRecipesUnlocked", false, "All recipes available. Off = not managed (the world's own key stands).",
                Opt.B("Every crafting recipe is unlocked from the start").Admin());
            _deathKeepEquip = BindSynced("DeathKeepEquip", false, "Keep equipped items on death. Off = not managed - a server launched with " +
                "'-modifier deathpenalty casual' keeps the key that flag set (this is GitHub issue #8).",
                Opt.B("Players keep their equipped gear when they die").Admin());
            _removeKeys = BindSynced("RemoveKeys", "",
                "Global keys to DELETE from the world every time these settings are applied (comma-separated " +
                "key names, e.g. 'enemyleveluprate,playerevents'). This is the ONLY setting that deletes a " +
                "world key - turning a flag above off leaves the world's key alone. Use it to clean up stray " +
                "keys - Valheim 1.0 renumbered its key list, so a mod built for the wrong game version writes " +
                "the wrong key (that is how 'enemyleveluprate 250' and 'playerevents 0' got onto a world on " +
                "2026-09-09). Keys named here are removed and never re-added by NoVikingLeftBehind; clear the " +
                "list afterwards.",
                Opt.T("Global keys to remove from the world").Admin());
        }

        protected override void ApplyPatches()
        {
            var target = AccessTools.Method(typeof(ZoneSystem), "SetStartingGlobalKeys", new[] { typeof(bool) });
            if (target == null)
                throw new Exception("NoVikingLeftBehind ServerKeys: ZoneSystem.SetStartingGlobalKeys(bool) not found");

            Harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ServerKeysModule), nameof(Postfix)));

            // Game.UpdateWorldRates(HashSet<string>, Dictionary<string,string>) - public static,
            // Game.cs:1340. ZoneSystem.UpdateWorldRates() (ZoneSystem.cs:795) is its only caller,
            // and every re-evaluation in the game goes through that one method: ZoneSystem.Start
            // (:694), GlobalKeyAdd (:767), GlobalKeyRemove (:788), ZoneSystem.Reset (:803),
            // ServerOptionsGUI (:160, :178) and Player respawn (:5105, :5134, :5144). Patching the
            // static covers all of them, on the server and on every client.
            var rates = AccessTools.Method(typeof(Game), "UpdateWorldRates",
                                           new[] { typeof(HashSet<string>), typeof(Dictionary<string, string>) });
            if (rates == null)
                throw new Exception("NoVikingLeftBehind ServerKeys: Game.UpdateWorldRates(HashSet<string>, Dictionary<string,string>) not found");

            Harmony.Patch(rates,
                postfix: new HarmonyMethod(typeof(ServerKeysModule), nameof(RatesPostfix)));

            _self = this;
        }

        public override void Disable()
        {
            base.Disable();
        }

        public override string StatusDetail()
        {
            if (_skillGainRate == null) return null;

            var sb = new StringBuilder();
            sb.Append("mode=").Append(RatesWithoutKeys ? "rates-without-world-keys" : "world-keys");
            sb.Append(" skillGain=").Append(_skillGainRate.Value.ToString(CultureInfo.InvariantCulture))
              .Append(IsDefault(_skillGainRate) ? "x(default,unmanaged)" : "x");
            sb.Append(" skillReduction=").Append(_skillReductionRate.Value.ToString(CultureInfo.InvariantCulture))
              .Append(IsDefault(_skillReductionRate) ? "x(default,unmanaged)" : "x");
            sb.Append(" effective=").Append(Game.m_skillGainRate.ToString("0.###"))
              .Append("/").Append(Game.m_skillReductionRate.ToString("0.###"));
            sb.Append(" achievement-safe: ").Append(AchievementSafety());
            return sb.ToString();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            // The rate statics are read live, so refresh them on every change (client included -
            // the module is Both, and a server push lands here as a SettingChanged).
            RefreshRates();

            if (!Active || !NoVikingLeftBehindPlugin.IsServerSide || ZoneSystem.instance == null)
            {
                if (Active && !NoVikingLeftBehindPlugin.IsServerSide)
                    Log.LogInfo("[ServerKeys] " + entry.Definition.Key + " changed; rates now " +
                                Game.m_skillGainRate.ToString("0.###") + "/" +
                                Game.m_skillReductionRate.ToString("0.###") +
                                " (world keys are the server's job)");
                else
                    Log.LogInfo("[ServerKeys] " + entry.Definition.Key + " changed, applied on next world load");
                return;
            }

            if (!_applyOnLoad.Value)
            {
                Log.LogInfo("[ServerKeys] " + entry.Definition.Key +
                            " changed but ApplyOnLoad=false, leaving the world's own keys alone");
                return;
            }

            try { Apply(); }
            catch (Exception e) { Log.LogError("[ServerKeys] re-apply after config change failed: " + e); }
        }

        private static void Postfix()
        {
            if (_self == null || !_self.Active) return;
            // The key half is the dedicated server's job only; a listen-server host never wrote
            // world keys and must not start now (it would flag the host's own world as cheated).
            if (!NoVikingLeftBehindPlugin.IsServerSide || !ServerActive()) return;

            if (!_self._applyOnLoad.Value)
            {
                Log.LogInfo("[ServerKeys] ApplyOnLoad=false, leaving the world's own keys alone");
                return;
            }

            try { _self.Apply(); }
            catch (Exception e) { Log.LogError("[ServerKeys] apply failed: " + e); }
        }

        // ---- rates without world keys ----------------------------------------------------

        private bool RatesWithoutKeys => _ratesWithoutWorldKeys == null || _ratesWithoutWorldKeys.Value;

        /// <summary>
        /// Runs after every vanilla re-evaluation of the world rates. Vanilla has just reset the
        /// statics from the world's keys (to 1.0 once our key is gone), so this is where the
        /// configured rate is put back - on the server and on every client.
        /// </summary>
        private static void RatesPostfix()
        {
            if (_self == null || !_self.Active) return;
            if (!_self.RatesWithoutKeys) return;

            // A rate left at its config default is not managed by us: whatever the world or a
            // launch flag decided stays exactly as vanilla computed it.
            if (!IsDefault(_self._skillGainRate)) Game.m_skillGainRate = _self._skillGainRate.Value;
            if (!IsDefault(_self._skillReductionRate)) Game.m_skillReductionRate = _self._skillReductionRate.Value;
        }

        /// <summary>Re-run vanilla's rate evaluation so <see cref="RatesPostfix"/> reloads the statics.</summary>
        private static void RefreshRates()
        {
            try
            {
                if (ZoneSystem.instance != null) ZoneSystem.instance.UpdateWorldRates();
            }
            catch (Exception e)
            {
                Log.LogWarning("[ServerKeys] rate refresh failed: " + e.Message);
            }
        }

        private static bool IsDefault(ConfigEntry<float> entry)
        {
            if (entry == null) return true;
            return Math.Abs(entry.Value - (float)entry.DefaultValue) < 0.0001f;
        }

        private void Apply()
        {
            var zs = ZoneSystem.instance;
            if (zs == null)
            {
                Log.LogWarning("[ServerKeys] ZoneSystem.instance is null, skipping");
                return;
            }

            if (RatesWithoutKeys)
            {
                DropStrayRateKey(zs, GlobalKeys.SkillGainRate, _skillGainRate);
                DropStrayRateKey(zs, GlobalKeys.SkillReductionRate, _skillReductionRate);
            }
            else
            {
                SetScalar(zs, GlobalKeys.SkillGainRate, _skillGainRate);
                SetScalar(zs, GlobalKeys.SkillReductionRate, _skillReductionRate);
            }

            SetFlag(zs, GlobalKeys.NoBuildCost, _noBuildCost.Value);
            SetFlag(zs, GlobalKeys.NoCraftCost, _noCraftCost.Value);
            SetFlag(zs, GlobalKeys.AllPiecesUnlocked, _allPiecesUnlocked.Value);
            SetFlag(zs, GlobalKeys.NoWorkbench, _noWorkbench.Value);
            SetFlag(zs, GlobalKeys.AllRecipesUnlocked, _allRecipesUnlocked.Value);
            SetFlag(zs, GlobalKeys.DeathKeepEquip, _deathKeepEquip.Value);

            // Stray keys the admin wants gone (see the RemoveKeys description). Removed by key
            // name; GlobalKeyRemove splits "name value" itself, so scalar keys work the same way.
            var remove = _removeKeys != null ? _removeKeys.Value : "";
            if (!string.IsNullOrEmpty(remove))
            {
                foreach (var raw in remove.Split(','))
                {
                    var name = raw.Trim().ToLowerInvariant();
                    if (name.Length == 0) continue;
                    if (!zs.GetGlobalKey(name)) continue;
                    bool removed = zs.GlobalKeyRemove(name, true);
                    Log.LogWarning("[ServerKeys] removed stray key '" + name + "' (RemoveKeys, removed=" + removed + ")");
                }
            }

            zs.UpdateWorldRates();

            var keys = new List<string>(zs.m_globalKeys);
            keys.Sort(StringComparer.Ordinal);
            Log.LogInfo("[ServerKeys] global keys now: [" + string.Join(", ", keys.ToArray()) + "]");
            Log.LogInfo("[ServerKeys] world rates: skillGain=" + Game.m_skillGainRate.ToString("0.###") +
                        " skillReduction=" + Game.m_skillReductionRate.ToString("0.###") +
                        " (mode=" + (RatesWithoutKeys ? "rates-without-world-keys" : "world-keys") + ")");
            Log.LogInfo("[ServerKeys] achievement-safe: " + AchievementSafety());
        }

        /// <summary>
        /// RatesWithoutWorldKeys migration: an older NoVikingLeftBehind stored the rate in the
        /// world file, which is what flags the world as cheated (issue #4). Drop it once, but
        /// only for a rate this install actually manages (a non-default value) and only when it
        /// is not a key the vanilla World Modifiers GUI could have produced.
        /// </summary>
        private static void DropStrayRateKey(ZoneSystem zs, GlobalKeys key, ConfigEntry<float> entry)
        {
            var name = key.ToString().ToLowerInvariant();
            if (!zs.GetGlobalKey(name)) return;
            if (IsDefault(entry))
            {
                Log.LogInfo("[ServerKeys] world key " + name + " left alone (" + entry.Definition.Key +
                            " is at its default, so it is not managed here)");
                return;
            }

            string stored;
            zs.m_globalKeysValues.TryGetValue(name, out stored);
            var full = (name + " " + (stored ?? "")).TrimEnd();
            if (IsVanillaModifierKey(full) == true)
            {
                Log.LogInfo("[ServerKeys] world key " + full + " kept (a vanilla world modifier set it)");
                return;
            }

            bool removed = zs.GlobalKeyRemove(name, true);
            if (removed)
                Log.LogWarning("[ServerKeys] removed world key " + name +
                               " (now applied without a world key, so achievements work again)");
        }

        private static void SetScalar(ZoneSystem zs, GlobalKeys key, ConfigEntry<float> entry)
        {
            var name = key.ToString().ToLowerInvariant();
            if (IsDefault(entry))
            {
                // Default = unmanaged: never write, never remove. A vanilla launch flag or the
                // world's own stored value survives untouched (issue #8).
                return;
            }

            // Game.UpdateWorldRates divides by 100, so store multiplier*100.
            float multiplier = entry.Value;
            float stored = multiplier * 100f;
            string keyStr = key + " " + stored.ToString(CultureInfo.InvariantCulture);

            string previous;
            bool had = zs.m_globalKeysValues.TryGetValue(name, out previous);
            if (had && previous != stored.ToString(CultureInfo.InvariantCulture))
                Log.LogWarning("[ServerKeys] overriding existing world key " + name + "=" + previous +
                               " with " + stored.ToString(CultureInfo.InvariantCulture) + " (config)");

            zs.GlobalKeyAdd(keyStr, true);
            Log.LogInfo("[ServerKeys] set " + keyStr + "  (=" + multiplier.ToString(CultureInfo.InvariantCulture) + "x)");
        }

        private static void SetFlag(ZoneSystem zs, GlobalKeys key, bool on)
        {
            // OFF means "not managed by NoVikingLeftBehind", NOT "delete". Deleting is what undid
            // '-modifier deathpenalty casual' on every world load (issue #8); [ServerKeys]
            // RemoveKeys is the explicit tool for taking a key off a world.
            if (!on) return;

            bool had = zs.GetGlobalKey(key);
            zs.GlobalKeyAdd(key.ToString(), true);
            if (!had) Log.LogInfo("[ServerKeys] set " + key);
        }

        // ---- diagnostics ------------------------------------------------------------------

        /// <summary>
        /// Is the World Modifiers GUI's prefab data loaded? It is what vanilla's own cheat check
        /// reads, and a dedicated server never instantiates the prefab (Game.UpdateWorldRates logs
        /// "Can't get world modifier summary until prefab has been initiated" there).
        /// </summary>
        private static bool GuiModifierDataLoaded()
        {
            try
            {
                var modifiers = ServerOptionsGUI.m_modifiers;
                return modifiers != null && modifiers.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Is this exact stored key ("skillreductionrate 0") one the vanilla World Modifiers GUI
        /// can produce? null = cannot tell, because the GUI's prefab data is not loaded (the
        /// normal case on a dedicated server).
        /// </summary>
        private static bool? IsVanillaModifierKey(string storedKey)
        {
            try
            {
                if (!GuiModifierDataLoaded()) return null;
                var modifiers = ServerOptionsGUI.m_modifiers;
                foreach (var ui in modifiers)
                {
                    var slider = ui as KeySlider;
                    if (slider != null && slider.m_settings != null)
                    {
                        foreach (var setting in slider.m_settings)
                            if (setting.m_keys != null && setting.m_keys.Contains(storedKey)) return true;
                    }
                    var toggle = ui as KeyToggle;
                    if (toggle != null && toggle.m_enabledKey == storedKey) return true;
                }
                return false;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// "yes"/"no"/"unknown" - does the saved world carry a starting key the vanilla World
        /// Modifiers GUI cannot produce? That is exactly the test Achievements runs
        /// (Achievements.cs -> ServerOptionsGUI.WorldContainsCheatedModifiers, ServerOptionsGUI.cs:281).
        /// Vanilla's own answer is used when the GUI data is loaded; a dedicated server has no
        /// GUI prefab, so there we answer the narrower question we can actually prove: does the
        /// world still carry a skill-rate key (the only non-GUI key this module ever wrote)?
        /// </summary>
        private static string AchievementSafety()
        {
            var world = ZNet.World;
            if (world == null || world.m_startingGlobalKeys == null) return "unknown (no world loaded)";

            var starting = new List<string>(world.m_startingGlobalKeys);
            starting.Sort(StringComparer.Ordinal);
            var listed = "[" + string.Join(", ", starting.ToArray()) + "]";

            if (GuiModifierDataLoaded())
            {
                bool cheated;
                try { cheated = ServerOptionsGUI.WorldContainsCheatedModifiers(world); }
                catch (Exception e) { return "unknown (" + e.GetType().Name + ") starting keys " + listed; }
                return (cheated ? "no" : "yes") + " (vanilla check over starting keys " + listed + ")";
            }

            foreach (var raw in starting)
            {
                var name = raw.Split(' ')[0].Trim().ToLowerInvariant();
                if (name == "skillgainrate" || name == "skillreductionrate")
                    return "no - starting keys still contain '" + raw + "' " + listed;
            }
            return "yes as far as this module can tell (no skill-rate key; starting keys " + listed + ")";
        }
    }
}
