using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Scalars: Game.UpdateWorldRates divides the stored value by 100 (Game.cs:1262-1268
    /// trySetScalarKey, multiplier 100f), so a 2.5x multiplier is stored as "skillgainrate 250".
    ///
    /// Keys persist into the world file (GlobalKeyAdd writes ZNet.World.m_startingGlobalKeys),
    /// which is exactly why a false bool must actively REMOVE the key: config is authoritative.
    ///
    /// Hot reload: once a world is loaded (ZoneSystem.instance != null), a live config edit
    /// re-runs Apply() immediately instead of waiting for the next world load - the same code
    /// path SetStartingGlobalKeys uses, so the log lines ("set ...", "global keys now: [...]")
    /// are identical either way.
    /// </summary>
    internal sealed class ServerKeysModule : FeatureModule
    {
        public override string Name => "ServerKeys";
        public override ModuleSide Side => ModuleSide.Server;
        public override string Section => "ServerKeys";
        public override string Theme => "World";
        public override string Hint => "Force world-wide skill rates, build costs, and death penalties";

        protected override Opt EnabledOpt => base.EnabledOpt.Admin();

        protected override string EnabledDescription =>
            "Apply the world modifiers below on world load. Config wins over whatever is " +
            "stored in the world file.";

        private ConfigEntry<bool> _applyOnLoad;
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
            // are synced so an admin sees the real values in the in-game config manager.
            _applyOnLoad = BindSynced("ApplyOnLoad", true,
                "Apply on every world load. Turn off to leave the world's own keys alone.",
                Opt.B("Apply these settings automatically every time the world loads").Admin());

            _skillGainRate = BindSynced("SkillGainRate", 1.0f,
                "Skill gain multiplier. 1.0 = vanilla, 2.5 = 2.5x. Stored in the world as value*100.",
                Opt.N("How fast players gain skill levels", 0, 10).Admin());
            _skillReductionRate = BindSynced("SkillReductionRate", 1.0f,
                "Skill loss on death multiplier. 1.0 = vanilla, 0 = no skill loss.",
                Opt.N("How much skill is lost when a player dies", 0, 5).Admin());

            _noBuildCost = BindSynced("NoBuildCost", false, "Building costs no resources.",
                Opt.B("Building structures costs no resources").Admin());
            _noCraftCost = BindSynced("NoCraftCost", false, "Crafting costs no resources.",
                Opt.B("Crafting items costs no resources").Admin());
            _allPiecesUnlocked = BindSynced("AllPiecesUnlocked", false, "All build pieces available.",
                Opt.B("Every building piece is unlocked from the start").Admin());
            _noWorkbench = BindSynced("NoWorkbench", false, "No crafting-station requirement for building.",
                Opt.B("Building doesn't require a nearby crafting station").Admin());
            _allRecipesUnlocked = BindSynced("AllRecipesUnlocked", false, "All recipes available.",
                Opt.B("Every crafting recipe is unlocked from the start").Admin());
            _deathKeepEquip = BindSynced("DeathKeepEquip", false, "Keep equipped items on death.",
                Opt.B("Players keep their equipped gear when they die").Admin());
            _removeKeys = BindSynced("RemoveKeys", "",
                "Global keys to DELETE from the world every time these settings are applied (comma-separated " +
                "key names, e.g. 'enemyleveluprate,playerevents'). Use it to clean up stray keys - Valheim 1.0 " +
                "renumbered its key list, so a mod built for the wrong game version writes the wrong key " +
                "(that is how 'enemyleveluprate 250' and 'playerevents 0' got onto a world on 2026-09-09). " +
                "Keys named here are removed and never re-added by NoVikingLeftBehind; clear the list afterwards.",
                Opt.T("Global keys to remove from the world").Admin());
        }

        protected override void ApplyPatches()
        {
            var target = AccessTools.Method(typeof(ZoneSystem), "SetStartingGlobalKeys", new[] { typeof(bool) });
            if (target == null)
                throw new Exception("NoVikingLeftBehind ServerKeys: ZoneSystem.SetStartingGlobalKeys(bool) not found");

            Harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ServerKeysModule), nameof(Postfix)));

            _self = this;
        }

        public override void Disable()
        {
            base.Disable();
        }

        public override string StatusDetail()
        {
            if (_skillGainRate == null) return null;
            return "skillGain=" + _skillGainRate.Value.ToString(CultureInfo.InvariantCulture) +
                   "x skillReduction=" + _skillReductionRate.Value.ToString(CultureInfo.InvariantCulture) + "x";
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (!Active || !ServerActive() || ZoneSystem.instance == null)
            {
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
            if (!ServerActive()) return;

            if (!_self._applyOnLoad.Value)
            {
                Log.LogInfo("[ServerKeys] ApplyOnLoad=false, leaving the world's own keys alone");
                return;
            }

            try { _self.Apply(); }
            catch (Exception e) { Log.LogError("[ServerKeys] apply failed: " + e); }
        }

        private void Apply()
        {
            var zs = ZoneSystem.instance;
            if (zs == null)
            {
                Log.LogWarning("[ServerKeys] ZoneSystem.instance is null, skipping");
                return;
            }

            SetScalar(zs, GlobalKeys.SkillGainRate, _skillGainRate.Value);
            SetScalar(zs, GlobalKeys.SkillReductionRate, _skillReductionRate.Value);

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
                        " skillReduction=" + Game.m_skillReductionRate.ToString("0.###"));
        }

        private static void SetScalar(ZoneSystem zs, GlobalKeys key, float multiplier)
        {
            // Game.UpdateWorldRates divides by 100, so store multiplier*100.
            float stored = multiplier * 100f;
            string keyStr = key + " " + stored.ToString(CultureInfo.InvariantCulture);
            zs.GlobalKeyAdd(keyStr, true);
            Log.LogInfo("[ServerKeys] set " + keyStr + "  (=" + multiplier.ToString(CultureInfo.InvariantCulture) + "x)");
        }

        private static void SetFlag(ZoneSystem zs, GlobalKeys key, bool on)
        {
            if (on)
            {
                zs.GlobalKeyAdd(key.ToString(), true);
                Log.LogInfo("[ServerKeys] set " + key);
            }
            else if (zs.GetGlobalKey(key))
            {
                bool removed = zs.GlobalKeyRemove(key.ToString(), true);
                Log.LogInfo("[ServerKeys] removed " + key + " (config says off, removed=" + removed + ")");
            }
        }
    }
}
