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
    /// * `ItemStand.Interact` (postfix) - latches WHICH slot the player asked for at the moment
    ///   they press the key, because vanilla only sets the power 2 s later
    ///   (`Invoke("DelayedPowerActivation", m_powerActivationDelay)`, ItemStand.decompiled.cs:221)
    ///   and by then the modifier is long released.
    /// * `ItemStand.DelayedPowerActivation` (prefix) - the boss-stone altar. Vanilla calls
    ///   Player.SetGuardianPower(m_guardianPower.name) here (ItemStand.decompiled.cs:248). Since
    ///   0.8.0 the mapping is the plain one: **interact = slot 1** (vanilla), SecondSlotModifier +
    ///   interact = slot 2, ThirdSlotModifier + interact = slot 3 (only when Slots = 3). When the
    ///   target is slot 1 we let vanilla run untouched so its per-boss PlayerStat bookkeeping
    ///   still happens.
    /// * `ItemStand.GetHoverText` (postfix) - vanilla's last tooltip line is
    ///   `[<color=yellow><b>$KEY_Use</b></color>] $guardianstone_hook_activate`
    ///   (ItemStand.decompiled.cs:136). We append one line per EXTRA slot in exactly that shape,
    ///   naming the configured modifier. Never throws: on any surprise the original text is kept.
    /// * `ItemStand.IsGuardianPowerActive` (postfix) - vanilla only compares against slot 1
    ///   (`(user as Player).GetGuardianPowerName() == m_guardianPower.name`), which is the right
    ///   answer for a plain interact. With a modifier held the question is really "is it in the
    ///   slot that modifier targets", so we answer that one instead.
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
        public override string Theme => "Combat & powers";
        public override string Hint => "Carry two Forsaken powers, each on its own cooldown";
        public override ModuleSide Side =>
            (_selfTest != null && _selfTest.Value) ? ModuleSide.Both : ModuleSide.Client;

        /// <summary>How long after our own activation an incoming animation event is ours.</summary>
        private const float SuppressWindow = 2f;

        private static DualPowersModule _inst;
        private static bool _selfTestDone;

        private ConfigEntry<int> _slots;
        private ConfigEntry<string> _secondSlotKey;
        private ConfigEntry<string> _thirdSlotKey;
        private ConfigEntry<string> _secondSlotModifier;
        private ConfigEntry<string> _thirdSlotModifier;
        /// <summary>Obsolete since 0.8.0. Bound only so an existing cfg still loads unchanged.</summary>
        private ConfigEntry<string> _slot1Modifier;
        private ConfigEntry<bool> _independentCooldowns;
        private ConfigEntry<float> _cooldownMultiplier;
        private ConfigEntry<bool> _showHud;
        private ConfigEntry<float> _hudOffsetX;
        private ConfigEntry<float> _hudOffsetY;
        private ConfigEntry<bool> _roundIcons;
        private ConfigEntry<bool> _cooldownRing;
        private ConfigEntry<float> _ringThickness;
        private ConfigEntry<string> _ringColor;
        private ConfigEntry<string> _ringTrackColor;
        private ConfigEntry<bool> _selfTest;

        /// <summary>Parsed hotkeys, index 0 -> slot 1 (the second slot).</summary>
        private static readonly KeyCode[] Keys = new KeyCode[PowerSlots.MaxSlots - 1];

        /// <summary>
        /// Altar modifiers, same indexing as <see cref="Keys"/>: index 0 -> slot 2, index 1 ->
        /// slot 3. Holding none means slot 1, which is vanilla's own behaviour.
        /// </summary>
        private static readonly KeyCode[] Mods = new KeyCode[PowerSlots.MaxSlots - 1];

        // The altar sets the power m_powerActivationDelay (2 s) AFTER the interact, so the slot the
        // player asked for has to be latched at interact time - nobody keeps a modifier held for
        // two seconds while the stone hums.
        private static ItemStand _pendingStand;
        private static int _pendingSlot;
        private static float _pendingUntil;

        // One-shot logging for the hover-text postfix; see HoverTextPostfix.
        private static bool _hoverLogged;
        private static bool _hoverShapeWarned;
        private static bool _hoverErrorLogged;

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
                "on SecondSlotKey. 3 works too but you must also set ThirdSlotKey.",
                Opt.N("How many Forsaken powers you can carry at once", 1, 3, 1));
            _independentCooldowns = BindSynced("IndependentCooldowns", true,
                "True: every slot has its own cooldown. False: one shared vanilla cooldown, so " +
                "using either power puts both on cooldown.",
                Opt.B("Give each power slot its own separate cooldown"));
            _cooldownMultiplier = BindSynced("CooldownMultiplier", 1f,
                "Multiplies the cooldown every guardian power starts, in every slot. " +
                "0.5 = half-length cooldowns, 1 = vanilla.",
                Opt.N("Multiplier applied to every power's cooldown length", 0, 5, 0.05));
            _secondSlotKey = BindLocal("SecondSlotKey", "G",
                "Machine-local. UnityEngine.KeyCode name for the second power slot (vanilla's F " +
                "always stays slot 1). Examples: G, H, LeftAlt, Mouse3, JoystickButton5.",
                Opt.T("Key that activates the second power slot"));
            _thirdSlotKey = BindLocal("ThirdSlotKey", "None",
                "Machine-local. KeyCode for a third slot, only used when Slots = 3. " +
                "'None' disables it.",
                Opt.T("Key that activates the third power slot"));
            _secondSlotModifier = BindLocal("SecondSlotModifier", "LeftShift",
                "Machine-local. Hold this while activating a boss altar to put the power in SLOT 2. " +
                "The whole altar scheme: interact on its own = slot 1, exactly " +
                "like vanilla; SecondSlotModifier + interact = slot 2; ThirdSlotModifier + interact " +
                "= slot 3 (only when Slots = 3). The slot is decided the moment you interact, so you " +
                "can let go while the stone charges. 'None' disables it. " +
                "LeftShift/LeftControl/LeftAlt also accept their right-hand twin.",
                Opt.T("Modifier key held at an altar to set the second power slot"));
            _thirdSlotModifier = BindLocal("ThirdSlotModifier", "LeftControl",
                "Machine-local. Hold this while activating a boss altar to put the power in SLOT 3. " +
                "Ignored unless Slots = 3. Same scheme as SecondSlotModifier: interact alone = slot 1, " +
                "SecondSlotModifier = slot 2, ThirdSlotModifier = slot 3. If you bind both to the " +
                "same key the lower slot wins. 'None' disables it.",
                Opt.T("Modifier key held at an altar to set the third power slot"));
            _slot1Modifier = BindLocal("Slot1Modifier", "LeftShift",
                "OBSOLETE - this setting does nothing at all any more. It is still here only so an " +
                "existing cfg keeps loading without an error. Up to 0.7.x a plain altar interact " +
                "filled the first EMPTY slot and this key forced slot 1, which felt backwards; from " +
                "0.8.0 a plain interact always sets slot 1 (vanilla) and the modifiers pick the extra " +
                "slots instead. Use SecondSlotModifier / ThirdSlotModifier. Safe to delete.",
                Opt.T("Obsolete - replaced by SecondSlotModifier and ThirdSlotModifier"));
            _showHud = BindLocal("ShowHud", true,
                "Machine-local. Clone the vanilla power icon so slot 2 gets its own icon, name " +
                "and cooldown readout. Turn off if it clashes with another HUD mod.",
                Opt.B("Show a separate HUD icon for the second power slot"));
            _hudOffsetX = BindLocal("HudOffsetX", 84f,
                "Machine-local. Pixels to move the second power icon sideways from the vanilla one. " +
                "Defaults to 84 so it sits beside the vanilla icon (~50px) instead of overlapping it.",
                Opt.N("Sideways offset of the second power icon, in pixels", -200, 200, 1));
            _hudOffsetY = BindLocal("HudOffsetY", 0f,
                "Machine-local. Pixels to move the second power icon vertically (negative = below, " +
                "positive = above). Defaults to 0, level with the vanilla icon - HudOffsetX already " +
                "keeps the two side by side so their name labels do not collide.",
                Opt.N("Vertical offset of the second power icon, in pixels", -200, 200, 1));
            _roundIcons = BindLocal("RoundIcons", true,
                "Machine-local, cosmetic. Draw every Forsaken power icon (slot 1 and the extra " +
                "slots) as a circle instead of vanilla's square. The vanilla icon sprite is never " +
                "replaced - it is masked to a circle, generated at runtime, no shipped assets.",
                Opt.B("Draw power icons as circles instead of squares"));
            _cooldownRing = BindLocal("CooldownRing", true,
                "Machine-local, cosmetic. Draw a thin charge ring around each power icon that " +
                "fills as the cooldown recovers - full ring = ready. Every second CombatRecharge " +
                "shaves off jumps the ring forward, so a fight visibly charges your power.",
                Opt.B("Show a charging ring around each power icon"));
            _ringThickness = BindLocal("RingThickness", 4f,
                "Machine-local, cosmetic. Ring thickness in pixels (1-24). ~4 suits 1080p; the " +
                "ring is drawn on the icon's own edge, so it scales with the HUD.",
                Opt.N("Thickness of the cooldown ring, in pixels", 1, 24, 1));
            _ringColor = BindLocal("RingColor", "E6C88AD9",
                "Machine-local, cosmetic. RGBA hex for the filled (recovered) part of the ring. " +
                "Default E6C88AD9 is Valheim's warm parchment gold at 85% alpha.",
                Opt.T("Colour of the filled part of the cooldown ring"));
            _ringTrackColor = BindLocal("RingTrackColor", "00000066",
                "Machine-local, cosmetic. RGBA hex for the un-filled remainder of the ring.",
                Opt.T("Colour of the empty part of the cooldown ring"));
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, machine-local. Runs the module on a dedicated server too and logs a " +
                "storage + recharge self test at world load. Leave false in normal use.",
                Opt.B("Run power-slot self tests on a dedicated server").Admin().Restart());

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
            Mods[0] = ParseKey(_secondSlotModifier.Value, KeyCode.LeftShift, "SecondSlotModifier");
            if (Mods.Length > 1) Mods[1] = ParseKey(_thirdSlotModifier.Value, KeyCode.LeftControl, "ThirdSlotModifier");

            // 0.8.1: the extra power keys are real, rebindable Valheim keybindings. The lambdas are
            // re-read on every registration, so the cfg supplies the DEFAULT and a player's rebind
            // on the Keyboard & Mouse page wins. The altar MODIFIERS stay ours: that page binds one
            // key per action and has no notion of a held modifier.
            NvlbKeys.Declare(PowerKeyId(1), "Power slot 2", delegate { return Keys[0]; });
            if (Keys.Length > 1)
                NvlbKeys.Declare(PowerKeyId(2), "Power slot 3", delegate { return Keys[1]; });

            PowerHud.SetOffset(new Vector2(_hudOffsetX.Value, _hudOffsetY.Value));
            // The whole point of 0.4.5's Powers half: the key is written ON the icon, so nobody has
            // to read the config to discover that the second power is on G.
            PowerHud.KeyLabel = KeyLabel(1);
            // 0.4.8: round icons + charge ring. Cosmetic and machine-local, so it is pushed the
            // same way as everything else and re-applies live on a config edit.
            PowerRing.Configure(
                _roundIcons.Value,
                _cooldownRing.Value,
                _ringThickness.Value,
                PowerRing.ParseColor(_ringColor.Value, new Color(0.902f, 0.784f, 0.541f, 0.851f), "RingColor"),
                PowerRing.ParseColor(_ringTrackColor.Value, new Color(0f, 0f, 0f, 0.4f), "RingTrackColor"));
        }

        /// <summary>Printable name of the key that fires a slot. Slot 0 asks ZInput for vanilla's own binding.</summary>
        internal static string KeyLabel(int slot)
        {
            if (slot == 0)
            {
                try
                {
                    var zi = ZInput.instance;
                    if (zi != null)
                    {
                        string s = zi.GetBoundKeyString("GP", true);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { /* fall through to the vanilla default */ }
                return "F";
            }
            if (slot < 1 || slot > Keys.Length) return "";
            // The BOUND key, which since 0.8.1 can differ from the configured one - it is what the
            // player will actually press, and this string is written onto the HUD icon.
            string bound = NvlbKeys.Label(PowerKeyId(slot));
            if (!string.IsNullOrEmpty(bound)) return bound;
            return Keys[slot - 1] == KeyCode.None ? "" : Keys[slot - 1].ToString();
        }

        /// <summary>The ZInput button id (without the NVLB_ prefix) for the extra power slot n+1.</summary>
        internal static string PowerKeyId(int slot) { return "PowerSlot" + (slot + 1); }

        /// <summary>
        /// Printable name of the modifier that targets <paramref name="slot"/> ("LeftShift" ->
        /// "Shift", "LeftControl" -> "Ctrl"). "" for slot 1 - it needs no modifier - and for a
        /// modifier set to None.
        /// </summary>
        internal static string ModifierLabel(int slot)
        {
            if (slot < 1 || slot > Mods.Length) return "";
            KeyCode k = Mods[slot - 1];
            if (k == KeyCode.None) return "";
            string s = k.ToString();
            if (s.StartsWith("Left", StringComparison.Ordinal)) s = s.Substring(4);
            else if (s.StartsWith("Right", StringComparison.Ordinal)) s = s.Substring(5);
            if (s == "Control") s = "Ctrl";
            return s;
        }

        /// <summary>Is <paramref name="k"/> down? Left/Right twins count as the same key.</summary>
        private static bool ModifierHeld(KeyCode k)
        {
            if (k == KeyCode.None) return false;
            if (ZInput.GetKey(k, false)) return true;
            KeyCode twin = k == KeyCode.LeftShift ? KeyCode.RightShift
                         : k == KeyCode.RightShift ? KeyCode.LeftShift
                         : k == KeyCode.LeftControl ? KeyCode.RightControl
                         : k == KeyCode.RightControl ? KeyCode.LeftControl
                         : k == KeyCode.LeftAlt ? KeyCode.RightAlt
                         : k == KeyCode.RightAlt ? KeyCode.LeftAlt
                         : KeyCode.None;
            return twin != KeyCode.None && ZInput.GetKey(twin, false);
        }

        /// <summary>
        /// Which slot the altar would target right now: 0 (plain interact, vanilla) unless one of
        /// the extra-slot modifiers is held. Slots the config does not enable are never returned,
        /// so at Slots = 1 this is always 0 and at Slots = 2 it is never 2.
        /// </summary>
        private static int TargetSlot()
        {
            int extras = PowerSlots.ExtraCount;
            if (extras > Mods.Length) extras = Mods.Length;
            for (int slot = 1; slot <= extras; slot++)
                if (ModifierHeld(Mods[slot - 1])) return slot;
            return 0;
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
            if (entry == EnabledCfg && !EnabledCfg.Value) { PowerHud.Destroy(); PowerRing.UndecorateAll(); }
            if (entry == EnabledCfg && EnabledCfg.Value) PowerRing.Reset();
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
            var standInteract = AccessTools.Method(typeof(ItemStand), "Interact", new[] { typeof(Humanoid), typeof(bool), typeof(bool) });
            if (standInteract == null) throw new Exception("ItemStand.Interact(Humanoid,bool,bool) not found");
            var standHover = AccessTools.Method(typeof(ItemStand), "GetHoverText");
            if (standHover == null) throw new Exception("ItemStand.GetHoverText() not found");
            if (AccessTools.Field(typeof(ItemStand), "m_guardianPower") == null)
                throw new Exception("ItemStand.m_guardianPower not found");
            if (AccessTools.Field(typeof(ItemStand), "m_canBeRemoved") == null)
                throw new Exception("ItemStand.m_canBeRemoved not found");
            if (AccessTools.Field(typeof(ItemStand), "m_powerActivationDelay") == null)
                throw new Exception("ItemStand.m_powerActivationDelay not found");
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
            if (AccessTools.Method(typeof(SEMan), "AddStatusEffect",
                                   new[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) }) == null)
                throw new Exception("SEMan.AddStatusEffect(int,bool,int,float,short) not found - slot 2 could not be applied");
            if (AccessTools.Method(typeof(Player), "GetPlayersInRange", new[] { typeof(Vector3), typeof(float), typeof(List<Player>) }) == null)
                throw new Exception("Player.GetPlayersInRange(Vector3,float,List<Player>) not found");

            var self = typeof(DualPowersModule);
            Harmony.Patch(stand, prefix: new HarmonyMethod(self, nameof(StandPrefix)));
            Harmony.Patch(standActive, postfix: new HarmonyMethod(self, nameof(StandActivePostfix)));
            Harmony.Patch(standInteract, postfix: new HarmonyMethod(self, nameof(StandInteractPostfix)));
            Harmony.Patch(standHover, postfix: new HarmonyMethod(self, nameof(HoverTextPostfix)));
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
                        " key1=" + KeyLabel(0) + " key2=" + Keys[0] +
                        (PowerSlots.SlotCount > 2 ? " key3=" + Keys[1] : "") +
                        " altar=" + AltarScheme() +
                        " independentCooldowns=" + PowerSlots.IndependentCooldowns +
                        " cooldownMultiplier=" + PowerSlots.CooldownMultiplier +
                        " hud=" + _showHud.Value + " storage=" + PowerSlots.NameKey(1) + "/" +
                        PowerSlots.CooldownKey(1) + " selfTest=" + _selfTest.Value);
            Log.LogInfo("[Powers] HUD ring: roundIcons=" + PowerRing.RoundIcons +
                        " cooldownRing=" + PowerRing.CooldownRing +
                        " thickness=" + PowerRing.Thickness + "px" +
                        " colour=#" + ColorUtility.ToHtmlStringRGBA(PowerRing.RingColor) +
                        " track=#" + ColorUtility.ToHtmlStringRGBA(PowerRing.TrackColor) +
                        " (the 'decorated' line follows on a client once the HUD lays out)");
            if (_slot1Modifier != null && _slot1Modifier.Value != (string)_slot1Modifier.DefaultValue)
                Log.LogWarning("[DualPowers] [Powers] Slot1Modifier = '" + _slot1Modifier.Value +
                               "' is obsolete and is IGNORED. A plain altar interact now sets slot 1 " +
                               "(vanilla); the extra slots are on SecondSlotModifier / ThirdSlotModifier.");
        }

        /// <summary>"interact=slot1 Shift=slot2 Ctrl=slot3", for the log and nvlb.status.</summary>
        private static string AltarScheme()
        {
            if (PowerSlots.ExtraCount <= 0) return "vanilla (Slots=1)";
            string s = "interact:slot1";
            for (int slot = 1; slot <= PowerSlots.ExtraCount && slot <= Mods.Length; slot++)
            {
                string m = ModifierLabel(slot);
                s += " " + (m.Length == 0 ? "(unbound)" : m + "+interact") + ":slot" + (slot + 1);
            }
            return s;
        }

        public override void Disable()
        {
            _pendingStand = null;
            PowerHud.Destroy();
            PowerRing.UndecorateAll();
            base.Disable();
        }

        // ---- altar routing ----------------------------------------------------------------

        /// <summary>
        /// Remember which slot the player asked for. Vanilla's Interact only *starts* the two-second
        /// activation (`Invoke("DelayedPowerActivation", m_powerActivationDelay)`), and it returns
        /// true only on the call that starts it - the "already invoking" and "already have it"
        /// paths both return false. So `__result &amp;&amp; IsInvoking(...)` is exactly "this press
        /// began an activation", and that is the instant the modifier state means something.
        /// </summary>
        private static void StandInteractPostfix(ItemStand __instance, Humanoid user, bool __result)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (PowerSlots.ExtraCount <= 0) return;              // Slots = 1 -> nothing to route
            try
            {
                if (!__result || __instance == null || __instance.m_guardianPower == null) return;
                var p = user as Player;
                if (p == null || p != Player.m_localPlayer) return;
                if (!__instance.IsInvoking("DelayedPowerActivation")) return;

                _pendingStand = __instance;
                _pendingSlot = TargetSlot();
                _pendingUntil = Time.time + Mathf.Max(0f, __instance.m_powerActivationDelay) + 2f;
            }
            catch (Exception e) { Log.LogWarning("[DualPowers] altar interact latch: " + e.Message); }
        }

        /// <summary>
        /// The slot this activation was started for. Normally the one latched at interact time; if
        /// that latch is missing or stale (another mod invoked the activation, a reload happened)
        /// fall back to reading the modifiers now, which is still better than guessing.
        /// </summary>
        private static int ConsumePendingSlot(ItemStand stand)
        {
            int slot;
            if (ReferenceEquals(_pendingStand, stand) && Time.time <= _pendingUntil) slot = _pendingSlot;
            else slot = TargetSlot();
            _pendingStand = null;
            return Mathf.Clamp(slot, 0, Mathf.Min(PowerSlots.ExtraCount, Mods.Length));
        }

        private static bool StandPrefix(ItemStand __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return true;
            // Slots = 1 is vanilla, full stop: no routing, no message, no extra hover line.
            if (PowerSlots.ExtraCount <= 0) return true;
            try
            {
                var me = Player.m_localPlayer;
                if (me == null || __instance == null || __instance.m_guardianPower == null) return true;
                PowerSlots.Bind(me);

                string power = __instance.m_guardianPower.name;
                string label = Localization.instance.Localize(__instance.m_guardianPower.m_name);

                // ---- the mapping, 0.8.0 -------------------------------------------------------
                //  * interact on its own       -> slot 1, exactly like vanilla
                //  * SecondSlotModifier + it   -> slot 2
                //  * ThirdSlotModifier  + it   -> slot 3 (only when Slots = 3)
                // Whatever happens, say so in the centre of the screen AND name the key, because
                // "which slot did that go in, and how do I fire it" was the whole complaint.
                int target = ConsumePendingSlot(__instance);
                int have = PowerSlots.FindSlot(me, power);

                if (have == target)
                {
                    // The gate in IsGuardianPowerActive normally stops this, but the player can let
                    // go of the modifier while the stone charges - then re-granting is a no-op.
                    me.Message(MessageHud.MessageType.Center,
                        label + " is already in slot " + (target + 1) + " (" + KeyLabel(target) + "). " +
                        HintLine());
                    return false;
                }

                // A power lives in exactly one slot: clear it wherever else it was sitting.
                if (have >= 0) PowerSlots.SetPower(me, have, "");

                if (target == 0)
                {
                    // Vanilla handles slot 1, including its per-boss PlayerStat bookkeeping, so let
                    // it run - we only add the message.
                    string had1 = PowerSlots.GetName(me, 0);
                    me.Message(MessageHud.MessageType.Center,
                        "Power of " + label + " set to slot 1 (" + KeyLabel(0) + ")" +
                        (string.IsNullOrEmpty(had1) ? "" : ", replacing " + PowerLabel(had1)) + ". " +
                        HintLine());
                    Log.LogInfo("[DualPowers] altar granted '" + power + "' to slot 1 (plain interact)" +
                                (string.IsNullOrEmpty(had1) ? "" : ", replacing '" + had1 + "'"));
                    return true;
                }

                string replaced = PowerSlots.GetName(me, target);
                PowerSlots.SetPower(me, target, power);
                try { Game.instance.IncrementPlayerStat(PlayerStatType.SetGuardianPower); }
                catch (Exception e) { Log.LogWarning("[DualPowers] stat increment failed: " + e.Message); }

                me.Message(MessageHud.MessageType.Center,
                    "Power of " + label + " set to slot " + (target + 1) + " (" + KeyLabel(target) + ")" +
                    (string.IsNullOrEmpty(replaced) ? "" : ", replacing " + PowerLabel(replaced)) + ".");
                Log.LogInfo("[DualPowers] altar granted '" + power + "' to slot " + (target + 1) +
                            " (" + ModifierLabel(target) + " held at interact)" +
                            (string.IsNullOrEmpty(replaced) ? "" : ", replacing '" + replaced + "'"));
                return false;
            }
            catch (Exception e)
            {
                Log.LogError("[DualPowers] altar routing failed, falling back to vanilla: " + e);
                return true;
            }
        }

        /// <summary>"Hold Shift for slot 2 (G)." - appended to the slot-1 altar messages.</summary>
        private static string HintLine()
        {
            string s = "";
            for (int slot = 1; slot <= PowerSlots.ExtraCount && slot <= Mods.Length; slot++)
            {
                string mod = ModifierLabel(slot);
                string key = KeyLabel(slot);
                if (mod.Length == 0 || key.Length == 0) continue;
                s += (s.Length == 0 ? "Hold " : ", ") + mod + " for slot " + (slot + 1) + " (" + key + ")";
            }
            return s.Length == 0 ? "" : s + ".";
        }

        /// <summary>Localised display name of a stored power name ("GP_Eikthyr" -> "Eikthyr").</summary>
        private static string PowerLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            try
            {
                if (ObjectDB.instance != null)
                {
                    var se = ObjectDB.instance.GetStatusEffect(name.GetStableHashCode());
                    if (se != null && !string.IsNullOrEmpty(se.m_name))
                        return Localization.instance.Localize(se.m_name);
                }
            }
            catch { /* fall through to the raw prefab name */ }
            return name;
        }

        /// <summary>
        /// Vanilla answers "is this stone's power already yours" by looking at slot 1 only, which is
        /// the right answer for a plain interact. With an extra-slot modifier held the real question
        /// is whether THAT slot already holds it, so answer that instead - which keeps the altar
        /// usable for a power you carry in another slot, and makes the hover text agree.
        /// </summary>
        private static void StandActivePostfix(ItemStand __instance, Humanoid user, ref bool __result)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (PowerSlots.ExtraCount <= 0) return;              // Slots = 1 -> vanilla's answer stands
            try
            {
                var p = user as Player;
                if (p == null || p != Player.m_localPlayer) return;
                if (__instance == null || __instance.m_guardianPower == null) return;

                int target = TargetSlot();
                if (target == 0) return;                         // plain interact -> vanilla is right
                PowerSlots.Bind(p);
                __result = PowerSlots.GetName(p, target) == __instance.m_guardianPower.name;
            }
            catch (Exception e) { Log.LogWarning("[DualPowers] IsGuardianPowerActive postfix: " + e.Message); }
        }

        // ---- altar hover text ---------------------------------------------------------------

        /// <summary>Vanilla's last tooltip line on a guardian stone (ItemStand.decompiled.cs:136).</summary>
        private const string VanillaActivateLine = "[<color=yellow><b>$KEY_Use</b></color>] $guardianstone_hook_activate";

        /// <summary>Vanilla's alternative last line when you already carry the power (line 134).</summary>
        private const string VanillaAlreadyLine = "$guardianstone_hook_alreadyactive";

        /// <summary>
        /// Append one hint line per EXTRA slot under vanilla's "[E] Activate power", in exactly the
        /// shape vanilla uses on that line. Anchored on the localised tail of the vanilla string, so
        /// if the game ever rewrites that line we simply add nothing rather than mangling it. Never
        /// throws: __result is only assigned once everything has been built.
        /// </summary>
        private static void HoverTextPostfix(ItemStand __instance, ref string __result)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (PowerSlots.ExtraCount <= 0) return;              // Slots = 1 -> exactly vanilla
            try
            {
                if (__instance == null || __instance.m_guardianPower == null) return;
                if (__instance.m_canBeRemoved) return;            // an ordinary item stand, not an altar
                if (string.IsNullOrEmpty(__result)) return;
                var me = Player.m_localPlayer;
                if (me == null) return;

                var loc = Localization.instance;
                if (loc == null) return;
                bool onActivate = __result.EndsWith(loc.Localize(VanillaActivateLine), StringComparison.Ordinal);
                bool onAlready = !onActivate &&
                                 __result.EndsWith(loc.Localize(VanillaAlreadyLine), StringComparison.Ordinal);
                if (!onActivate && !onAlready)
                {
                    // A warded stone is vanilla's third shape and perfectly normal - stay quiet.
                    if (__result.EndsWith(loc.Localize("$piece_noaccess"), StringComparison.Ordinal)) return;
                    // Anything else means a game patch moved the line we anchor on. Say so once.
                    if (!_hoverShapeWarned)
                    {
                        _hoverShapeWarned = true;
                        Log.LogWarning("[DualPowers] boss-stone hover text does not end in the vanilla " +
                                       "activate/already line, so no slot hints were added. Tail seen: '" +
                                       Tail(__result) + "'");
                    }
                    return;
                }

                PowerSlots.Bind(me);
                string power = __instance.m_guardianPower.name;
                string extra = "";
                for (int slot = 1; slot <= PowerSlots.ExtraCount && slot <= Mods.Length; slot++)
                {
                    string line = HoverSlotLine(loc, me, slot, power);
                    if (line.Length != 0) extra += "\n" + line;
                }
                if (extra.Length == 0) return;

                __result = __result + extra;
                if (!_hoverLogged)
                {
                    _hoverLogged = true;
                    Log.LogInfo("[DualPowers] altar hover hints active: " + AltarScheme());
                }
            }
            catch (Exception e)
            {
                // A hover postfix must never take the tooltip down with it.
                if (!_hoverErrorLogged)
                {
                    _hoverErrorLogged = true;
                    Log.LogWarning("[DualPowers] hover text postfix failed, leaving vanilla text: " + e);
                }
            }
        }

        /// <summary>One "[Shift + E] Set as second power" line, or "" when that slot has no modifier.</summary>
        private static string HoverSlotLine(Localization loc, Player me, int slot, string power)
        {
            string mod = ModifierLabel(slot);
            if (mod.Length == 0) return "";                       // modifier set to None -> no hint
            string what = (slot == 1) ? "second" : "third";
            string held = PowerSlots.GetName(me, slot);
            if (held == power)
                return "Already your " + what + " power";
            string line = loc.Localize("[<color=yellow><b>" + mod + " + $KEY_Use</b></color>] Set as " +
                                       what + " power");
            if (!string.IsNullOrEmpty(held)) line += " (replaces " + PowerLabel(held) + ")";
            return line;
        }

        private static string Tail(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 60 ? s : s.Substring(s.Length - 60);
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
                    // Through NvlbKeys since 0.8.1: these are real ZInput buttons now, so a rebind
                    // on Valheim's own Keyboard & Mouse page takes effect at once. It falls back to
                    // the configured KeyCode whenever registration did not happen.
                    if (NvlbKeys.Down(PowerKeyId(slot))) _inst.TryActivate(__instance, slot);
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
        internal static bool InputAllowed(Player me)
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
            if (player == null || player != Player.m_localPlayer) return;
            PowerSlots.Bind(player);
            if (_inst._showHud.Value && PowerSlots.ExtraCount > 0)
                PowerHud.Refresh(__instance, player, 1);
            // The ring/round-icon decoration is independent of the extra slot: it applies to
            // vanilla's own widget even at Slots=1 or ShowHud=false. It must run AFTER
            // PowerHud.Refresh, which needs a pristine m_gpRoot to clone (see PowerRing).
            PowerRing.Refresh(__instance, player);
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
            var keys = new List<string>();
            for (int s = 0; s < Mathf.Clamp(PowerSlots.SlotCount, 1, PowerSlots.MaxSlots); s++)
                keys.Add("slot" + (s + 1) + ":" + (KeyLabel(s).Length == 0 ? "-" : KeyLabel(s)));
            return slots +
                   "  keys=" + string.Join("/", keys.ToArray()) +
                   " altar=" + AltarScheme() +
                   " independent=" + PowerSlots.IndependentCooldowns +
                   " cdx" + PowerSlots.CooldownMultiplier +
                   " uses=" + _activations +
                   "  hud=round:" + PowerRing.RoundIcons + "/ring:" + PowerRing.CooldownRing +
                   "@" + PowerRing.Thickness + "px";
        }

        // ---- player actions the console exposes (nvlb.power clear/swap) ------------------------

        /// <summary>Empty one slot. Slot 0 is vanilla's. Returns what was there, "" when nothing.</summary>
        internal static string ClearSlot(Player me, int slot)
        {
            if (me == null) return "";
            string had = PowerSlots.GetName(me, slot);
            PowerSlots.SetPower(me, slot, "");
            if (slot != 0) PowerSlots.SetCooldown(me, slot, 0f);
            else me.m_guardianPowerCooldown = 0f;
            return had ?? "";
        }

        /// <summary>Exchange slot 1 and slot 2, cooldowns included.</summary>
        internal static void SwapSlots(Player me)
        {
            if (me == null) return;
            string a = PowerSlots.GetName(me, 0);
            string b = PowerSlots.GetName(me, 1);
            float ca = PowerSlots.GetCooldown(me, 0);
            float cb = PowerSlots.GetCooldown(me, 1);
            PowerSlots.SetPower(me, 0, b);
            PowerSlots.SetPower(me, 1, a);
            PowerSlots.SetCooldown(me, 0, cb);
            PowerSlots.SetCooldown(me, 1, ca);
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

            // (4) the 0.4.8 HUD decoration: the circle/ring coverage maths is pure, so it proves
            // itself headlessly; the Texture2D/Sprite build is attempted too and only reported.
            PowerRing.LogSelfTest();

            Log.LogInfo("[DualPowers] SelfTest: config slots=" + PowerSlots.SlotCount +
                        " independent=" + PowerSlots.IndependentCooldowns +
                        " cdMultiplier=" + PowerSlots.CooldownMultiplier +
                        " key2=" + Keys[0] + " altar=" + AltarScheme());
            Log.LogInfo("[DualPowers] SelfTest: --- end ---");
        }
    }
}
