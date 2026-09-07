using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **DualPowers** - carry two Forsaken powers at once, each with its own cooldown.
    ///
    /// Vanilla keeps exactly one power on the Player (m_guardianPower / m_guardianSE) and one
    /// cooldown float (m_guardianPowerCooldown), both written into the .fch profile by
    /// Player.Save/Load. We keep that as **slot 1** and add slot 2 (and, if you raise Slots, a
    /// third) in Player.m_customData - see PowerSlots for the storage contract.
    ///
    /// What is patched, and why
    /// ------------------------
    /// * `ItemStand.DelayedPowerActivation` (prefix) - the boss-stone altar. Vanilla calls
    ///   Player.SetGuardianPower(m_guardianPower.name) here (ItemStand.decompiled.cs:248). We route
    ///   it: first empty slot, else replace the LAST slot, and say which one it went to. When the
    ///   target is slot 1 we let vanilla run untouched so its per-boss PlayerStat bookkeeping
    ///   still happens.
    /// * `ItemStand.IsGuardianPowerActive` (postfix) - vanilla only compares against slot 1, so
    ///   without this the altar would happily re-grant a power you already hold in slot 2.
    /// * `Player.Update` (postfix) - reads the SecondSlotKey hotkey. Vanilla's F stays exactly as
    ///   it is: `ZInput.GetButtonDown("GP")` -> Player.StartGuardianPower() -> slot 1.
    /// * `Player.ActivateGuardianPower` (prefix + postfix) - this is the method the *animation
    ///   event* on the "gpower" clip calls; it is what actually applies the effect. The prefix
    ///   swallows the event that our own slot-2 activation triggered (otherwise it would fire
    ///   slot 1 a moment later); the postfix applies CooldownMultiplier to a normal slot-1 use.
    /// * `Player.UpdateGuardianPower` (postfix) - ticks the extra cooldowns with the very same dt
    ///   vanilla uses for its own, so the two can never drift.
    /// * `Player.Save` (prefix) / `Player.Load` (postfix) - flush to / restore from custom data.
    /// * `Player.ResetCharacter` (postfix) - vanilla zeroes m_guardianPowerCooldown there; mirror it.
    /// * `Hud.UpdateGuardianPower` (postfix) - refresh the cloned second HUD widget (PowerHud).
    /// * `ObjectDB.Awake` / `ObjectDB.CopyOtherDB` (postfix) - drop cached StatusEffect pointers,
    ///   because CopyOtherDB swaps the whole status-effect list out from under us.
    ///
    /// Side is Client. `[Powers] SelfTest` (local, default false) flips it to Both so the headless
    /// server can prove the storage round-trip and the recharge maths with no players connected;
    /// every runtime body is additionally gated on ClientActive(), so it stays inert there.
    /// </summary>
    internal sealed class DualPowersModule : FeatureModule
    {
        public override string Name => "DualPowers";
        public override string Section => "Powers";
        public override ModuleSide Side =>
            (_selfTest != null && _selfTest.Value) ? ModuleSide.Both : ModuleSide.Client;

        /// <summary>How long after our own activation an incoming animation event is ours.</summary>
        private const float SuppressWindow = 2f;

        private static DualPowersModule _inst;
        private static bool _selfTestDone;

        private ConfigEntry<int> _slots;
        private ConfigEntry<string> _secondSlotKey;
        private ConfigEntry<string> _thirdSlotKey;
        private ConfigEntry<bool> _independentCooldowns;
        private ConfigEntry<float> _cooldownMultiplier;
        private ConfigEntry<bool> _showHud;
        private ConfigEntry<float> _hudOffsetX;
        private ConfigEntry<float> _hudOffsetY;
        private ConfigEntry<bool> _selfTest;

        /// <summary>Parsed hotkeys, index 0 -> slot 1 (the second slot).</summary>
        private static readonly KeyCode[] Keys = new KeyCode[PowerSlots.MaxSlots - 1];

        private static float _suppressUntil;
        private static bool _skippedVanilla;
        private static float _cdBeforeActivate;
        private static int _activations;
        private static int _tickErrors;

        // ---- public API used by CombatRecharge -------------------------------------------

        /// <summary>True when DualPowers is patched in and switched on.</summary>
        public static bool IsActive { get { return _inst != null && _inst.Active; } }

        /// <summary>
        /// Shorten every EXTRA slot's cooldown by <paramref name="seconds"/>. Safe no-op when the
        /// module is off, when Slots = 1, or when cooldowns are shared (slot 1's timer is then the
        /// only one and CombatRecharge already shortened it).
        /// </summary>
        public static void ReduceCooldowns(float seconds)
        {
            if (!IsActive || seconds <= 0f) return;
            PowerSlots.ReduceCooldowns(seconds);
        }

        // ---- config ----------------------------------------------------------------------

        protected override void Bind()
        {
            _slots = BindSynced("Slots", 2,
                "How many Forsaken powers you can hold at once. 1 = vanilla. 2 = the second slot " +
                "on SecondSlotKey. 3 works too but you must also set ThirdSlotKey.");
            _independentCooldowns = BindSynced("IndependentCooldowns", true,
                "True: every slot has its own cooldown. False: one shared vanilla cooldown, so " +
                "using either power puts both on cooldown.");
            _cooldownMultiplier = BindSynced("CooldownMultiplier", 1f,
                "Multiplies the cooldown every guardian power starts, in every slot. " +
                "0.5 = half-length cooldowns, 1 = vanilla.");
            _secondSlotKey = BindLocal("SecondSlotKey", "G",
                "Machine-local. UnityEngine.KeyCode name for the second power slot (vanilla's F " +
                "always stays slot 1). Examples: G, H, LeftAlt, Mouse3, JoystickButton5.");
            _thirdSlotKey = BindLocal("ThirdSlotKey", "None",
                "Machine-local. KeyCode for a third slot, only used when Slots = 3. " +
                "'None' disables it.");
            _showHud = BindLocal("ShowHud", true,
                "Machine-local. Clone the vanilla power icon so slot 2 gets its own icon, name " +
                "and cooldown readout. Turn off if it clashes with another HUD mod.");
            _hudOffsetX = BindLocal("HudOffsetX", 0f,
                "Machine-local. Pixels to move the second power icon sideways from the vanilla one.");
            _hudOffsetY = BindLocal("HudOffsetY", -56f,
                "Machine-local. Pixels to move the second power icon vertically (negative = below).");
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, machine-local. Runs the module on a dedicated server too and logs a " +
                "storage + recharge self test at world load. Leave false in normal use.");

            _inst = this;
            Push();
        }

        private void Push()
        {
            PowerSlots.SlotCount = Mathf.Clamp(_slots.Value, 1, PowerSlots.MaxSlots);
            PowerSlots.IndependentCooldowns = _independentCooldowns.Value;
            PowerSlots.CooldownMultiplier = Mathf.Clamp(_cooldownMultiplier.Value, 0f, 100f);
            Keys[0] = ParseKey(_secondSlotKey.Value, KeyCode.G, "SecondSlotKey");
            if (Keys.Length > 1) Keys[1] = ParseKey(_thirdSlotKey.Value, KeyCode.None, "ThirdSlotKey");
            PowerHud.SetOffset(new Vector2(_hudOffsetX.Value, _hudOffsetY.Value));
        }

        private static KeyCode ParseKey(string s, KeyCode fallback, string what)
        {
            if (string.IsNullOrEmpty(s)) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); }
            catch
            {
                Log.LogWarning("[DualPowers] " + what + " = '" + s + "' is not a UnityEngine.KeyCode name, " +
                               "falling back to " + fallback + ".");
                return fallback;
            }
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            Push();
            if (entry == _showHud && !_showHud.Value) PowerHud.Destroy();
            if (entry == _showHud && _showHud.Value) PowerHud.Reset();
            if (entry == EnabledCfg && !EnabledCfg.Value) PowerHud.Destroy();
            if (entry == _selfTest)
                Log.LogInfo("[DualPowers] SelfTest=" + _selfTest.Value +
                            " takes effect on the next game start (module side is decided at load).");
        }

        // ---- patches ---------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var stand = AccessTools.Method(typeof(ItemStand), "DelayedPowerActivation");
            if (stand == null) throw new Exception("ItemStand.DelayedPowerActivation() not found");
            var standActive = AccessTools.Method(typeof(ItemStand), "IsGuardianPowerActive", new[] { typeof(Humanoid) });
            if (standActive == null) throw new Exception("ItemStand.IsGuardianPowerActive(Humanoid) not found");
            var update = AccessTools.Method(typeof(Player), "Update");
            if (update == null) throw new Exception("Player.Update() not found");
            var activate = AccessTools.Method(typeof(Player), "ActivateGuardianPower");
            if (activate == null) throw new Exception("Player.ActivateGuardianPower() not found");
            var tick = AccessTools.Method(typeof(Player), "UpdateGuardianPower", new[] { typeof(float) });
            if (tick == null) throw new Exception("Player.UpdateGuardianPower(float) not found");
            var save = AccessTools.Method(typeof(Player), "Save", new[] { typeof(ZPackage) });
            if (save == null) throw new Exception("Player.Save(ZPackage) not found");
            var load = AccessTools.Method(typeof(Player), "Load", new[] { typeof(ZPackage) });
            if (load == null) throw new Exception("Player.Load(ZPackage) not found");
            var reset = AccessTools.Method(typeof(Player), "ResetCharacter");
            if (reset == null) throw new Exception("Player.ResetCharacter() not found");
            var hudGp = AccessTools.Method(typeof(Hud), "UpdateGuardianPower", new[] { typeof(Player) });
            if (hudGp == null) throw new Exception("Hud.UpdateGuardianPower(Player) not found");
            var odbAwake = AccessTools.Method(typeof(ObjectDB), "Awake");
            if (odbAwake == null) throw new Exception("ObjectDB.Awake() not found");
            var odbCopy = AccessTools.Method(typeof(ObjectDB), "CopyOtherDB", new[] { typeof(ObjectDB) });
            if (odbCopy == null) throw new Exception("ObjectDB.CopyOtherDB(ObjectDB) not found");

            // The vanilla pieces we call instead of patching - fail loudly if the game renamed them.
            if (AccessTools.Method(typeof(Player), "SetGuardianPower", new[] { typeof(string) }) == null)
                throw new Exception("Player.SetGuardianPower(string) not found");
            if (AccessTools.Method(typeof(SEMan), "AddStatusEffect", new[] { typeof(int), typeof(bool), typeof(int), typeof(float) }) == null)
                throw new Exception("SEMan.AddStatusEffect(int,bool,int,float) not found - slot 2 could not be applied");
            if (AccessTools.Method(typeof(Player), "GetPlayersInRange", new[] { typeof(Vector3), typeof(float), typeof(List<Player>) }) == null)
                throw new Exception("Player.GetPlayersInRange(Vector3,float,List<Player>) not found");

            var self = typeof(DualPowersModule);
            Harmony.Patch(stand, prefix: new HarmonyMethod(self, nameof(StandPrefix)));
            Harmony.Patch(standActive, postfix: new HarmonyMethod(self, nameof(StandActivePostfix)));
            Harmony.Patch(update, postfix: new HarmonyMethod(self, nameof(PlayerUpdatePostfix)));
            Harmony.Patch(activate,
                prefix: new HarmonyMethod(self, nameof(ActivatePrefix)),
                postfix: new HarmonyMethod(self, nameof(ActivatePostfix)));
            Harmony.Patch(tick, postfix: new HarmonyMethod(self, nameof(TickPostfix)));
            Harmony.Patch(save, prefix: new HarmonyMethod(self, nameof(SavePrefix)));
            Harmony.Patch(load, postfix: new HarmonyMethod(self, nameof(LoadPostfix)));
            Harmony.Patch(reset, postfix: new HarmonyMethod(self, nameof(ResetPostfix)));
            Harmony.Patch(hudGp, postfix: new HarmonyMethod(self, nameof(HudPostfix)));
            var odbPost = new HarmonyMethod(self, nameof(ObjectDBPostfix));
            Harmony.Patch(odbAwake, postfix: odbPost);
            Harmony.Patch(odbCopy, postfix: odbPost);

            Log.LogInfo("[DualPowers] slots=" + PowerSlots.SlotCount +
                        " key2=" + Keys[0] + (PowerSlots.SlotCount > 2 ? " key3=" + Keys[1] : "") +
                        " independentCooldowns=" + PowerSlots.IndependentCooldowns +
                        " cooldownMultiplier=" + PowerSlots.CooldownMultiplier +
                        " hud=" + _showHud.Value + " storage=" + PowerSlots.NameKey(1) + "/" +
                        PowerSlots.CooldownKey(1) + " selfTest=" + _selfTest.Value);
        }

        public override void Disable()
        {
            PowerHud.Destroy();
            base.Disable();
        }

        // ---- altar routing ----------------------------------------------------------------

        private static bool StandPrefix(ItemStand __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return true;
            try
            {
                var me = Player.m_localPlayer;
                if (me == null || __instance == null || __instance.m_guardianPower == null) return true;
                PowerSlots.Bind(me);

                string power = __instance.m_guardianPower.name;
                string label = Localization.instance.Localize(__instance.m_guardianPower.m_name);

                int have = PowerSlots.FindSlot(me, power);
                if (have >= 0)
                {
                    me.Message(MessageHud.MessageType.Center, label + " is already in power slot " + (have + 1));
                    return false;
                }

                int target = PowerSlots.FirstEmptySlot(me);
                if (target < 0) target = PowerSlots.ExtraCount;   // every slot full -> replace the last
                if (target == 0) return true;                     // vanilla handles slot 1 (and its stats)

                string replaced = PowerSlots.GetName(me, target);
                PowerSlots.SetPower(me, target, power);
                try { Game.instance.IncrementPlayerStat(PlayerStatType.SetGuardianPower); }
                catch (Exception e) { Log.LogWarning("[DualPowers] stat increment failed: " + e.Message); }

                me.Message(MessageHud.MessageType.Center,
                    label + " -> power slot " + (target + 1) +
                    (string.IsNullOrEmpty(replaced) ? "" : " (replaced " + replaced + ")"));
                Log.LogInfo("[DualPowers] altar granted '" + power + "' to slot " + (target + 1) +
                            (string.IsNullOrEmpty(replaced) ? "" : ", replacing '" + replaced + "'"));
                return false;
            }
            catch (Exception e)
            {
                Log.LogError("[DualPowers] altar routing failed, falling back to vanilla: " + e);
                return true;
            }
        }

        private static void StandActivePostfix(ItemStand __instance, Humanoid user, ref bool __result)
        {
            if (__result || _inst == null || !_inst.Active || !ClientActive()) return;
            try
            {
                var p = user as Player;
                if (p == null || p != Player.m_localPlayer || __instance.m_guardianPower == null) return;
                if (PowerSlots.FindSlot(p, __instance.m_guardianPower.name) >= 0) __result = true;
            }
            catch (Exception e) { Log.LogWarning("[DualPowers] IsGuardianPowerActive postfix: " + e.Message); }
        }

        // ---- input ------------------------------------------------------------------------

        private static void PlayerUpdatePostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            PowerSlots.Bind(__instance);
            if (PowerSlots.ExtraCount <= 0) return;

            try
            {
                if (!InputAllowed(__instance)) return;
                for (int slot = 1; slot <= PowerSlots.ExtraCount; slot++)
                {
                    var key = Keys[slot - 1];
                    if (key == KeyCode.None) continue;
                    if (ZInput.GetKeyDown(key, false)) _inst.TryActivate(__instance, slot);
                }
            }
            catch (Exception e)
            {
                if (_tickErrors++ < 3) Log.LogError("[DualPowers] input tick failed (" + _tickErrors + "/3): " + e);
            }
        }

        /// <summary>
        /// ZInput.GetKeyDown reads the raw Input System device, so - unlike ZInput.GetButtonDown -
        /// it does NOT know about chat, the console or an open menu. Do that gating ourselves.
        /// </summary>
        private static bool InputAllowed(Player me)
        {
            if (!me.TakeInput()) return false;
            if (Hud.InRadial() || Hud.IsPieceSelectionVisible()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            if (Console.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || StoreGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen()) return false;
            return true;
        }

        // ---- activation --------------------------------------------------------------------

        /// <summary>
        /// Replicates Player.StartGuardianPower + Player.ActivateGuardianPower for an extra slot.
        ///
        /// Vanilla splits the two: F runs StartGuardianPower (the gate checks, the "gpower"
        /// animation trigger) and the *animation event* on that clip later calls
        /// ActivateGuardianPower, which is what shares the effect. We do the gate checks, apply the
        /// effect immediately (so slot 2 works even if the animation event never lands), then fire
        /// the same animation trigger for the visual and arm the suppression window so the event
        /// it produces is swallowed instead of firing slot 1.
        ///
        /// The group share is byte-for-byte what vanilla does (Player.cs:5927-5931):
        /// Player.GetPlayersInRange(pos, 10 m, list), then for each of them
        /// GetSEMan().AddStatusEffect(hash, resetTime: true). SEMan.AddStatusEffect applies it
        /// locally when we own the character, and otherwise sends the vanilla
        /// ZNetView RPC "RPC_AddStatusEffect" to its owner (SEMan.decompiled.cs:137-149) - so
        /// party members get the buff over the game's own network path, with no custom RPC and no
        /// requirement that they run the mod.
        /// </summary>
        private void TryActivate(Player me, int slot)
        {
            var se = PowerSlots.GetSe(me, slot);
            if (se == null)
            {
                string nm = PowerSlots.GetName(me, slot);
                me.Message(MessageHud.MessageType.Center,
                    string.IsNullOrEmpty(nm)
                        ? "No power in slot " + (slot + 1)
                        : "Power '" + nm + "' in slot " + (slot + 1) + " is unknown to this game");
                return;
            }
            if (PowerSlots.GetCooldown(me, slot) > 0f)
            {
                me.Message(MessageHud.MessageType.Center, "$hud_powernotready");
                return;
            }
            // Exactly vanilla's StartGuardianPower() gate (Player.cs:5871).
            if ((me.InAttack() && !me.HaveQueuedChain()) || me.InDodge() || !me.CanMove() ||
                me.IsKnockedBack() || me.IsStaggering() || me.InMinorAction())
                return;

            var list = new List<Player>();
            Player.GetPlayersInRange(me.transform.position, 10f, list);
            int hash = se.NameHash();
            foreach (var p in list)
            {
                if (p == null) continue;
                var seman = p.GetSEMan();
                if (seman != null) seman.AddStatusEffect(hash, true);
            }

            try { if (me.m_adrenalineGuardianPower != 0f) me.AddAdrenaline(me.m_adrenalineGuardianPower); }
            catch (Exception e) { Log.LogWarning("[DualPowers] AddAdrenaline failed: " + e.Message); }

            PowerSlots.SetCooldown(me, slot, se.m_cooldown * PowerSlots.CooldownMultiplier);
            _activations++;

            try { Game.instance.IncrementPlayerStat(PlayerStatType.UseGuardianPower); }
            catch (Exception e) { Log.LogWarning("[DualPowers] stat increment failed: " + e.Message); }

            _suppressUntil = Time.time + SuppressWindow;
            try { me.m_zanim.SetTrigger("gpower"); }
            catch (Exception e) { Log.LogWarning("[DualPowers] gpower animation trigger failed: " + e.Message); }

            Log.LogInfo("[DualPowers] slot " + (slot + 1) + " '" + se.name + "' used on " + list.Count +
                        " player(s), cooldown " + (se.m_cooldown * PowerSlots.CooldownMultiplier).ToString("0") + "s");
        }

        /// <summary>Swallow the animation event our own slot-2 activation produced.</summary>
        private static bool ActivatePrefix(Player __instance, ref bool __result)
        {
            _skippedVanilla = false;
            if (_inst == null || !_inst.Active || __instance == null || __instance != Player.m_localPlayer)
                return true;
            if (_suppressUntil > 0f && Time.time <= _suppressUntil)
            {
                _suppressUntil = 0f;
                _skippedVanilla = true;
                __result = false;
                return false;
            }
            _suppressUntil = 0f;
            _cdBeforeActivate = __instance.m_guardianPowerCooldown;
            return true;
        }

        /// <summary>Apply CooldownMultiplier to a genuine vanilla (slot 1) activation.</summary>
        private static void ActivatePostfix(Player __instance)
        {
            if (_skippedVanilla) { _skippedVanilla = false; return; }
            if (_inst == null || !_inst.Active || __instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                // Vanilla only writes the cooldown when it actually fired the power.
                if (_cdBeforeActivate <= 0f && __instance.m_guardianPowerCooldown > 0f)
                {
                    if (PowerSlots.CooldownMultiplier != 1f)
                        __instance.m_guardianPowerCooldown *= PowerSlots.CooldownMultiplier;
                    if (!PowerSlots.IndependentCooldowns)
                        PowerSlots.SetCooldown(__instance, 0, __instance.m_guardianPowerCooldown);
                    _activations++;
                }
            }
            catch (Exception e) { Log.LogWarning("[DualPowers] activate postfix: " + e.Message); }
        }

        // ---- cooldown tick, persistence, HUD -------------------------------------------------

        private static void TickPostfix(Player __instance, float dt)
        {
            if (_inst == null || !_inst.Active || __instance == null || __instance != Player.m_localPlayer) return;
            PowerSlots.Bind(__instance);
            PowerSlots.Tick(__instance, dt);
        }

        private static void SavePrefix(Player __instance)
        {
            if (_inst == null || !_inst.Active || __instance == null) return;
            try { if (PowerSlots.IsOwner(__instance)) PowerSlots.SaveTo(__instance); }
            catch (Exception e) { Log.LogWarning("[DualPowers] save flush failed: " + e.Message); }
        }

        private static void LoadPostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || __instance == null) return;
            try
            {
                PowerSlots.LoadFrom(__instance);
                Log.LogInfo("[DualPowers] loaded " + PowerSlots.Describe(__instance));
            }
            catch (Exception e) { Log.LogWarning("[DualPowers] load failed: " + e.Message); }
        }

        private static void ResetPostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active) return;
            PowerSlots.ResetCooldowns();
        }

        private static void HudPostfix(Hud __instance, Player player)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (!_inst._showHud.Value || PowerSlots.ExtraCount <= 0) return;
            if (player == null || player != Player.m_localPlayer) return;
            PowerSlots.Bind(player);
            PowerHud.Refresh(__instance, player, 1);
        }

        private static void ObjectDBPostfix(ObjectDB __instance)
        {
            if (_inst == null || !_inst.Active) return;
            PowerSlots.InvalidateStatusEffects();
            if (_inst._selfTest.Value) RunSelfTest(__instance);
        }

        // ---- nvlb.status ---------------------------------------------------------------------

        public override string StatusDetail()
        {
            var me = Player.m_localPlayer;
            string slots = (me != null) ? PowerSlots.Describe(me) : "slots=(no local player)";
            return slots +
                   "  key2=" + Keys[0] + (PowerSlots.SlotCount > 2 ? " key3=" + Keys[1] : "") +
                   " independent=" + PowerSlots.IndependentCooldowns +
                   " cdx" + PowerSlots.CooldownMultiplier +
                   " uses=" + _activations;
        }

        // ---- self test (headless, 0 players) ----------------------------------------------------

        private static void RunSelfTest(ObjectDB odb)
        {
            if (_selfTestDone) return;
            // ObjectDB.Awake fires first on a nearly empty DB; the real one arrives on a later
            // Awake / CopyOtherDB (92+ status effects on the dedicated server). Wait for it.
            if (odb == null || odb.m_StatusEffects == null || odb.m_StatusEffects.Count < 10) return;
            _selfTestDone = true;

            Log.LogInfo("[DualPowers] SelfTest: --- begin ---");

            // (1) every guardian power the game knows about.
            int found = 0;
            if (odb != null && odb.m_StatusEffects != null)
            {
                foreach (var se in odb.m_StatusEffects)
                {
                    if (se == null || se.name == null || !se.name.StartsWith("GP_", StringComparison.Ordinal)) continue;
                    found++;
                    Log.LogInfo("[DualPowers] SelfTest: power '" + se.name + "' m_name=" + se.m_name +
                                " cooldown=" + se.m_cooldown + "s hash=" + se.NameHash());
                }
            }
            Log.LogInfo("[DualPowers] SelfTest: " + found + " GP_* status effects in ObjectDB (" +
                        (odb != null && odb.m_StatusEffects != null ? odb.m_StatusEffects.Count : 0) + " total)");

            // (2) storage round trip through the same dictionary Player.Save/Load persists.
            var data = new Dictionary<string, string>();
            data[PowerSlots.NameKey(1)] = "GP_Bonemass";
            data[PowerSlots.CooldownKey(1)] = PowerSlots.EncodeCooldown(123.456f);
            float back = PowerSlots.DecodeCooldown(data[PowerSlots.CooldownKey(1)]);
            Log.LogInfo("[DualPowers] SelfTest: storage keys '" + PowerSlots.NameKey(1) + "'='" +
                        data[PowerSlots.NameKey(1)] + "' '" + PowerSlots.CooldownKey(1) + "'='" +
                        data[PowerSlots.CooldownKey(1)] + "' -> decoded " + back +
                        (Mathf.Abs(back - 123.456f) < 0.01f ? "  OK" : "  *** FAIL ***"));
            Log.LogInfo("[DualPowers] SelfTest: decode('') = " + PowerSlots.DecodeCooldown("") +
                        ", decode('nonsense') = " + PowerSlots.DecodeCooldown("nonsense") +
                        ", decode('-5') = " + PowerSlots.DecodeCooldown("-5") +
                        ", encode(0) = '" + PowerSlots.EncodeCooldown(0f) +
                        "', encode(-1) = '" + PowerSlots.EncodeCooldown(-1f) + "'  (all must be 0)");
            Log.LogInfo("[DualPowers] SelfTest: slot keys 1.." + (PowerSlots.MaxSlots - 1) + " = " +
                        PowerSlots.NameKey(1) + "/" + PowerSlots.CooldownKey(1) + ", " +
                        PowerSlots.NameKey(2) + "/" + PowerSlots.CooldownKey(2));

            // (3) hand the combat-recharge maths a 300 s cooldown, 5 hits dealt + 2 taken.
            CombatRechargeModule.LogSelfTest(300f, 5, 2);

            Log.LogInfo("[DualPowers] SelfTest: config slots=" + PowerSlots.SlotCount +
                        " independent=" + PowerSlots.IndependentCooldowns +
                        " cdMultiplier=" + PowerSlots.CooldownMultiplier +
                        " key2=" + Keys[0]);
            Log.LogInfo("[DualPowers] SelfTest: --- end ---");
        }
    }
}
