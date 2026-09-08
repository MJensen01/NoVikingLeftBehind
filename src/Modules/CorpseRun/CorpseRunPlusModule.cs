using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// CorpseRunPlus - four independent quality-of-life features for the run back to your corpse.
    /// Client side only. Each feature has its own Enabled sub-flag under [CorpseRun].
    ///
    ///  1. GraveCompass   - a HUD arrow + distance pointing at your death point (GraveCompassHud).
    ///  2. RespawnFood    - one free food slot on a death-respawn (never on login).
    ///  3. RespawnRested  - a guaranteed Rested buff on a death-respawn.
    ///  4. GravePull      - a distance-scaled stamina buff while the corpse is far away
    ///                      (GravePullStatusEffect).
    ///  5. CorpseRunScaled- vanilla's 50 s "CorpseRun" loot buff, stretched by how far the grave
    ///                      was from home.
    ///
    /// Harmony patches (all verified against the 0.221.12 decompile):
    ///
    ///  | Target                          | Kind    | Why                                          |
    ///  | ObjectDB.Awake                  | postfix | register NVLB_GravePull in m_StatusEffects    |
    ///  | ObjectDB.CopyOtherDB(ObjectDB)  | postfix | CopyOtherDB *replaces* m_StatusEffects with   |
    ///  |                                 |         | the other DB's list (ObjectDB:30), so Awake   |
    ///  |                                 |         | alone would be silently thrown away          |
    ///  | Player.Update                   | postfix | the tick: compass, pull strength, deferred    |
    ///  |                                 |         | respawn grants, deferred CorpseRun scaling    |
    ///  | Player.OnDeath                  | postfix | "died this session" flag + remember the grave |
    ///  |                                 |         | (SetDeathPoint has already run, Player:3117)  |
    ///  | Player.OnSpawned(bool)          | postfix | arm the respawn grants (Game.SpawnPlayer:436) |
    ///  | TombStone.GiveBoost             | postfix | grave emptied on its owner's client: the      |
    ///  |                                 |         | vanilla CorpseRun SE was just added (:206)    |
    ///  | TombStone.OnTakeAllSuccess      | postfix | grave looted on the looter's client (:112)    |
    ///
    /// Everything the buffs actually *do* rides on vanilla StatusEffect virtuals - no patch on any
    /// stamina, damage or food hot path. Mobs are untouched: enemies still aggro exactly as before.
    ///
    /// Side is Client. [CorpseRun] SelfTest (machine-local, default false) flips it to Both so a
    /// headless dedicated server can prove ObjectDB registration and the maths with zero players;
    /// the tick is still gated on ClientActive() and stays inert there.
    /// </summary>
    internal sealed class CorpseRunPlusModule : FeatureModule
    {
        public override string Name => "CorpseRunPlus";
        public override string Section => "CorpseRun";
        public override string Theme => "Death";
        public override string Hint => "Compass, food, rest and stamina help getting back to your corpse";

        // Configure() runs before TryEnable(), so reading the config entry here is safe.
        public override ModuleSide Side =>
            (_selfTest != null && _selfTest.Value) ? ModuleSide.Both : ModuleSide.Client;

        internal static readonly int PullHash = GravePullStatusEffect.SeName.GetStableHashCode();
        internal static readonly int RestedHash = "Rested".GetStableHashCode();

        private static CorpseRunPlusModule _inst;
        private static GravePullStatusEffect _pullTemplate;
        private static bool _selfTestDone;

        // ---- config ----------------------------------------------------------------------

        private ConfigEntry<bool> _compassEnabled;
        private ConfigEntry<float> _compassHideDistance;
        private ConfigEntry<float> _compassUpdateSec;
        private ConfigEntry<float> _compassOffsetX;
        private ConfigEntry<float> _compassOffsetY;
        private ConfigEntry<string> _compassArrow;
        private ConfigEntry<float> _compassArrowScale;
        private ConfigEntry<string> _compassMode;
        private ConfigEntry<float> _compassEdgeMargin;
        private ConfigEntry<float> _hintAlpha;
        private ConfigEntry<float> _hintScale;
        private ConfigEntry<string> _clearGraveKey;
        private ConfigEntry<float> _clearGraveHoldSec;

        private ConfigEntry<bool> _respawnFoodEnabled;
        private ConfigEntry<string> _respawnFoods;
        private ConfigEntry<int> _respawnFoodCount;

        private ConfigEntry<bool> _respawnRestedEnabled;
        private ConfigEntry<float> _restedMinutes;

        private ConfigEntry<bool> _pullEnabled;
        private ConfigEntry<float> _pullMinDistance;
        private ConfigEntry<float> _pullFullDistance;
        private ConfigEntry<float> _pullMaxRegenBonus;
        private ConfigEntry<float> _pullMaxDrainReduction;
        private ConfigEntry<float> _pullUpdateSec;
        private ConfigEntry<string> _pullIconFrom;

        private ConfigEntry<bool> _scaledEnabled;
        private ConfigEntry<float> _scaledDurationPer100m;
        private ConfigEntry<float> _scaledMaxDurationSec;
        private ConfigEntry<float> _scaledExtraRegen;
        private ConfigEntry<float> _scaledRegenFullDistance;

        private ConfigEntry<float> _lootMatchDistance;
        private ConfigEntry<bool> _selfTest;

        // ---- runtime state ---------------------------------------------------------------

        private static bool _diedThisSession;
        private static bool _looted;
        private static Vector3 _grave;
        private static bool _haveGrave;
        private static string _graveWorld = "";
        /// <summary>grave-to-home distance captured at loot time, before the record is deleted.</summary>
        private static float _lootDistanceFromHome = -1f;
        private static bool _commandRegistered;

        private static bool _grantPending;
        private static float _grantAt;

        private static bool _scalePending;
        private static float _scalePendingUntil;
        private static int _corpseRunHash;
        private static StatusEffect _lastScaled;

        private static float _compassAcc;
        private static KeyCode _clearKey = KeyCode.Delete;
        private static float _clearHeld;
        private static float _pullAcc;

        private static float _lastDistance = -1f;
        private static float _lastStrength;
        private static bool _pullOn;
        private static string _lastGrantText = "none";
        private static float _lastScaledTtl;

        private static MethodInfo _updateFood;

        private CorpseRunPlusModule() { _inst = this; }

        // ---- config ----------------------------------------------------------------------

        protected override void Bind()
        {
            _selfTest = BindLocal("SelfTest", false,
                "Machine-local diagnostic: flip the module's Side to Both so a dedicated server " +
                "registers the status effect and runs the self test (top-10 stamina foods, the " +
                "vanilla Rested/CorpseRun fields, and the pull/duration maths) with zero players. " +
                "Never synced. Leave false in normal play.",
                Opt.B("Prove the corpse-run buffs work with no players online").Admin().Restart());

            _compassEnabled = BindSynced("CompassEnabled", true,
                "GraveCompass: show a HUD arrow and distance pointing at your death point until " +
                "you reach or loot the grave.",
                Opt.B("Show a compass pointing at your death point"));
            _compassHideDistance = BindSynced("CompassHideDistance", 10f,
                "GraveCompass: hide the compass once you are this close to the grave, in metres.",
                Opt.N("Metres from the grave before the compass hides", 0, 50));
            _compassUpdateSec = BindSynced("CompassUpdateSec", 0.25f,
                "GraveCompass: seconds between compass refreshes.",
                Opt.N("Seconds between compass position refreshes", 0.05, 2));
            _compassOffsetX = BindLocal("CompassOffsetX", 0f,
                "GraveCompass: horizontal position of the compass, in HUD units from the centre " +
                "of the screen. Machine-local - it is a personal HUD preference.",
                Opt.N("Horizontal position of the compass on screen", -500, 500));
            _compassOffsetY = BindLocal("CompassOffsetY", 200f,
                "GraveCompass: vertical position of the compass, in HUD units from the centre of " +
                "the screen (positive = up). Machine-local.",
                Opt.N("Vertical position of the compass on screen", -500, 500));
            _compassArrow = BindLocal("CompassArrow", "^",
                "GraveCompass: the character used as the arrow. It is rotated to point at the " +
                "grave. \"^\" is ASCII and always renders; a nicer glyph may not exist in the font.",
                Opt.T("Character used as the compass arrow"));
            _compassArrowScale = BindLocal("CompassArrowScale", 1.6f,
                "GraveCompass: arrow font size as a multiple of the donor label's size.",
                Opt.N("Size of the compass arrow relative to text", 0.5, 5));

            _respawnFoodEnabled = BindSynced("RespawnFoodEnabled", true,
                "RespawnFood: put food in your belly when you respawn after a death (never on " +
                "login). The item is created from the prefab - it is not taken from any inventory.",
                Opt.B("Give free food when you respawn after dying"));
            _respawnFoods = BindSynced("RespawnFoods", "Bread",
                "RespawnFood: comma-separated item prefab names, best first. Only the first " +
                "RespawnFoodCount that exist in ObjectDB are used (Valheim allows 3 food slots).",
                Opt.T("Which foods to grant on a death-respawn, in order"));
            _respawnFoodCount = BindSynced("RespawnFoodCount", 1,
                "RespawnFood: how many of the RespawnFoods entries to grant, 0-3.",
                Opt.N("How many foods to grant on a death-respawn", 0, 3));

            _respawnRestedEnabled = BindSynced("RespawnRestedEnabled", true,
                "RespawnRested: give the vanilla Rested buff on a death-respawn, with at least " +
                "RestedMinutes left on it.",
                Opt.B("Give the Rested buff when you respawn after dying"));
            _restedMinutes = BindSynced("RestedMinutes", 10f,
                "RespawnRested: minimum minutes of Rested granted on a death-respawn. Vanilla's " +
                "base is 5 minutes plus 1 per comfort level; this raises it, never lowers it.",
                Opt.N("Minimum minutes of Rested granted on respawn", 0, 60));

            _pullEnabled = BindSynced("PullEnabled", true,
                "GravePull: a stamina buff that scales with how far your corpse still is. " +
                "Affects only your own stamina - enemies are completely untouched.",
                Opt.B("Give a stamina buff while your corpse is far away"));
            _pullMinDistance = BindSynced("PullMinDistance", 50f,
                "GravePull: no buff at all within this many metres of the grave.",
                Opt.N("Metres from the grave before the stamina buff starts", 0, 500));
            _pullFullDistance = BindSynced("PullFullDistance", 1000f,
                "GravePull: metres BEYOND PullMinDistance at which the buff reaches full strength.",
                Opt.N("Extra metres beyond the start distance for full strength", 0, 5000));
            _pullMaxRegenBonus = BindSynced("PullMaxRegenBonus", 1f,
                "GravePull: extra stamina regeneration at full strength (1.0 = +100%).",
                Opt.N("Extra stamina regeneration at full strength", 0, 5));
            _pullMaxDrainReduction = BindSynced("PullMaxDrainReduction", 0.5f,
                "GravePull: fraction of run and jump stamina cost removed at full strength " +
                "(0.5 = half price).",
                Opt.N("Run and jump stamina cost cut at full strength", 0, 1, 0.05));
            _pullUpdateSec = BindSynced("PullUpdateSec", 1f,
                "GravePull: seconds between strength recalculations.",
                Opt.N("Seconds between stamina-buff strength recalculations", 0.1, 10));
            _pullIconFrom = BindSynced("PullIconFrom", "Rested",
                "GravePull: borrow this status effect's HUD icon. The mod ships no art. " +
                "'CorpseRun' is the other obvious choice.",
                Opt.T("Status effect to borrow the stamina-buff icon from"));

            _scaledEnabled = BindSynced("ScaledEnabled", true,
                "CorpseRunScaled: stretch vanilla's 'CorpseRun' loot buff by how far the grave was " +
                "from your bed or home point, so a long death costs less than a short one.",
                Opt.B("Stretch the loot buff duration based on grave distance"));
            _scaledDurationPer100m = BindSynced("ScaledDurationPer100m", 0.2f,
                "CorpseRunScaled: extra duration per 100 m from home, as a fraction of the vanilla " +
                "duration (0.2 = +20% per 100 m).",
                Opt.N("Extra loot-buff duration per 100 metres from home", 0, 2));
            _scaledMaxDurationSec = BindSynced("ScaledMaxDurationSec", 900f,
                "CorpseRunScaled: hard cap on the stretched duration, in seconds.",
                Opt.N("Maximum length the loot buff can be stretched to", 0, 3600));
            _scaledExtraRegen = BindSynced("ScaledExtraRegen", 0.5f,
                "CorpseRunScaled: extra stamina-regen multiplier added at ScaledRegenFullDistance " +
                "and beyond, scaled linearly by distance from home. 0 disables the strengthening.",
                Opt.N("Extra stamina regen added at the full distance", 0, 3));
            _scaledRegenFullDistance = BindSynced("ScaledRegenFullDistance", 1000f,
                "CorpseRunScaled: distance from home, in metres, at which ScaledExtraRegen is " +
                "applied in full.",
                Opt.N("Distance from home where the extra regen is full", 0, 5000));

            _compassMode = BindLocal("CompassMode", "Edge",
                "Machine-local. Edge = the marker is an off-screen waypoint: it sits on the grave " +
                "while the grave is on screen and slides to the screen edge in its direction when " +
                "it is not, so it is only ever dead centre when you are walking straight at it. " +
                "Fixed = the pre-0.4.5 behaviour, a static arrow at CompassOffsetX/Y.",
                Opt.C("How the compass marker behaves off-screen", "Edge", "Fixed"));

            _compassEdgeMargin = BindLocal("CompassEdgeMargin", 60f,
                "Machine-local. Pixels of inset kept between the marker and the edge of the screen " +
                "in Edge mode.",
                Opt.N("Pixel gap kept between the marker and the screen edge", 0, 300));

            _hintAlpha = BindLocal("HintAlpha", 0.55f,
                "Machine-local. How solid the 'hold Delete to dismiss' hint under the distance is: " +
                "1 = as bright as the distance, lower = more faded.",
                Opt.N("Opacity of the dismiss hint relative to the distance label", 0, 1));

            _hintScale = BindLocal("HintScale", 0.8f,
                "Machine-local. Size of that hint line relative to the distance label.",
                Opt.N("Size of the dismiss hint relative to the distance label", 0.5, 1.2));

            _clearGraveKey = BindLocal("ClearGraveKey", "Delete",
                "Machine-local. HOLD this key (see ClearGraveHoldSec) to dismiss the grave marker " +
                "and Grave Pull without opening the console - the same thing nvlb.grave.clear does. " +
                "A UnityEngine.KeyCode name; 'None' disables it. Ignored while a menu, the map, " +
                "chat or the console has your input.",
                Opt.T("Key held to dismiss the grave marker"));

            _clearGraveHoldSec = BindLocal("ClearGraveHoldSec", 1.5f,
                "Machine-local. Seconds ClearGraveKey must be held before the grave is dismissed. " +
                "Long enough that a stray keypress cannot lose your grave marker.",
                Opt.N("Seconds to hold the key to dismiss the grave", 0, 10));

            _lootMatchDistance = BindSynced("LootMatchDistance", 20f,
                "How close a tombstone must be to your recorded death point to count as YOUR " +
                "grave when it is looted or emptied. Guards against another player's grave " +
                "clearing your compass.",
                Opt.N("Metres a tombstone must be from your grave to count", 0, 200));
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            PushNumbers();
            GraveCompassHud.SetOffset(new Vector2(_compassOffsetX.Value, _compassOffsetY.Value),
                                      _compassArrow.Value, _compassArrowScale.Value);
            if (!_compassEnabled.Value || !Active) GraveCompassHud.Hide();
            if (!_pullEnabled.Value || !Active) RemovePull();
            Log.LogInfo("[CorpseRun] config: " + Numbers());
        }

        private void PushNumbers()
        {
            GravePullStatusEffect.MaxRegenBonus = _pullMaxRegenBonus.Value;
            GravePullStatusEffect.MaxDrainReduction = _pullMaxDrainReduction.Value;
            if (_pullTemplate != null)
            {
                float t = _pullUpdateSec.Value * 3f;
                _pullTemplate.m_ttl = t < 3f ? 3f : t;
            }
            GraveCompassHud.Offset = new Vector2(_compassOffsetX.Value, _compassOffsetY.Value);
            GraveCompassHud.ArrowChar = string.IsNullOrEmpty(_compassArrow.Value) ? "^" : _compassArrow.Value;
            GraveCompassHud.ArrowScale = _compassArrowScale.Value;
            GraveCompassHud.EdgeMode = !string.Equals(_compassMode.Value, "Fixed",
                                                      StringComparison.OrdinalIgnoreCase);
            GraveCompassHud.EdgeMargin = Mathf.Max(0f, _compassEdgeMargin.Value);
            GraveCompassHud.HintAlpha = _hintAlpha.Value;
            GraveCompassHud.HintScale = _hintScale.Value;
            _clearKey = ParseKey(_clearGraveKey.Value);

            // 0.8.1: a real, rebindable Valheim keybinding, so Delete can be moved on the game's
            // own Keyboard & Mouse page rather than only in a cfg file.
            NvlbKeys.Declare("GraveDismiss", "Dismiss grave (hold)", delegate { return _clearKey; });
        }

        /// <summary>
        /// What the dismiss key is bound to right now - the BOUND key, which since 0.8.1 can differ
        /// from [CorpseRun] ClearGraveKey because the binding is rebindable on Valheim's own
        /// Keyboard &amp; Mouse page. "" means nothing is bound, which switches the hint and the
        /// hold check off entirely.
        /// </summary>
        private static string DismissKeyLabel()
        {
            var s = NvlbKeys.Label("GraveDismiss");
            if (!string.IsNullOrEmpty(s)) return s;
            return _clearKey == KeyCode.None ? "" : _clearKey.ToString();
        }

        /// <summary>KeyCode name -> KeyCode; an unparsable name disables the hotkey rather than throwing.</summary>
        private static KeyCode ParseKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); }
            catch
            {
                Log.LogWarning("[CorpseRun] ClearGraveKey = '" + s + "' is not a UnityEngine.KeyCode " +
                               "name - the hold-to-dismiss hotkey is off.");
                return KeyCode.None;
            }
        }

        private string Numbers()
        {
            return "compass=" + _compassEnabled.Value + "(" + _compassMode.Value + " margin " +
                   _compassEdgeMargin.Value + "px, hide<" + _compassHideDistance.Value +
                   "m every " + _compassUpdateSec.Value + "s, dismiss=hold " + DismissKeyLabel() + " " +
                   _clearGraveHoldSec.Value + "s, hint alpha " + _hintAlpha.Value + " scale " +
                   _hintScale.Value + ")" +
                   " food=" + _respawnFoodEnabled.Value + "(" + _respawnFoods.Value + " x" +
                   _respawnFoodCount.Value + ")" +
                   " rested=" + _respawnRestedEnabled.Value + "(" + _restedMinutes.Value + "min)" +
                   " pull=" + _pullEnabled.Value + "(>" + _pullMinDistance.Value + "m ramp " +
                   _pullFullDistance.Value + "m regen+" +
                   Mathf.RoundToInt(_pullMaxRegenBonus.Value * 100f) + "% drain-" +
                   Mathf.RoundToInt(_pullMaxDrainReduction.Value * 100f) + "% every " +
                   _pullUpdateSec.Value + "s icon=" + _pullIconFrom.Value + ")" +
                   " scaled=" + _scaledEnabled.Value + "(+" +
                   Mathf.RoundToInt(_scaledDurationPer100m.Value * 100f) + "%/100m cap " +
                   _scaledMaxDurationSec.Value + "s regen+" + _scaledExtraRegen.Value + "@" +
                   _scaledRegenFullDistance.Value + "m)" +
                   " selfTest=" + _selfTest.Value;
        }

        // ---- patches ---------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var awake = AccessTools.Method(typeof(ObjectDB), "Awake");
            if (awake == null) throw new Exception("ObjectDB.Awake() not found");
            var copy = AccessTools.Method(typeof(ObjectDB), "CopyOtherDB", new[] { typeof(ObjectDB) });
            if (copy == null) throw new Exception("ObjectDB.CopyOtherDB(ObjectDB) not found");
            var update = AccessTools.Method(typeof(Player), "Update");
            if (update == null) throw new Exception("Player.Update() not found");
            var onDeath = AccessTools.Method(typeof(Player), "OnDeath");
            if (onDeath == null) throw new Exception("Player.OnDeath() not found");
            var onSpawned = AccessTools.Method(typeof(Player), "OnSpawned", new[] { typeof(bool) });
            if (onSpawned == null) throw new Exception("Player.OnSpawned(bool) not found");
            var giveBoost = AccessTools.Method(typeof(TombStone), "GiveBoost");
            if (giveBoost == null) throw new Exception("TombStone.GiveBoost() not found");
            var takeAll = AccessTools.Method(typeof(TombStone), "OnTakeAllSuccess");
            if (takeAll == null) throw new Exception("TombStone.OnTakeAllSuccess() not found");

            // Vanilla funnels the buffs ride on instead of being patched. If a game update removes
            // one, fail loudly here rather than shipping a buff that quietly does nothing.
            foreach (var sig in new[] { "ModifyStaminaRegen", "ModifyRunStaminaDrain", "ModifyJumpStaminaUsage" })
                if (AccessTools.Method(typeof(SEMan), sig) == null)
                    throw new Exception("SEMan." + sig + " not found - GravePull would be inert");

            // PlayerProfile's DEATH point is deliberately not used any more (see GraveRecord) -
            // only its home / custom spawn point, which CorpseRunScaled measures the run against.
            if (AccessTools.Method(typeof(PlayerProfile), "GetHomePoint") == null ||
                AccessTools.Method(typeof(PlayerProfile), "GetCustomSpawnPoint") == null)
                throw new Exception("PlayerProfile home point API not found");

            // ZNet.GetWorldName() is what makes a grave record belong to ONE world.
            if (AccessTools.Method(typeof(ZNet), "GetWorldName") == null)
                throw new Exception("ZNet.GetWorldName() not found - the grave record could not be scoped to a world");

            var termInit = AccessTools.Method(typeof(Terminal), "InitTerminal");
            if (termInit == null) throw new Exception("Terminal.InitTerminal() not found");

            if (AccessTools.Method(typeof(Player), "GetFoods") == null)
                throw new Exception("Player.GetFoods() not found - RespawnFood would be inert");
            _updateFood = AccessTools.Method(typeof(Player), "UpdateFood",
                                             new[] { typeof(float), typeof(bool) });
            if (_updateFood == null)
                Log.LogWarning("[CorpseRun] Player.UpdateFood(float,bool) not found - granted food " +
                               "will show up on the HUD a second late instead of immediately.");

            var odbPost = new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(ObjectDBPostfix));
            Harmony.Patch(awake, postfix: odbPost);
            Harmony.Patch(copy, postfix: odbPost);
            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(PlayerUpdatePostfix)));
            Harmony.Patch(onDeath, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(OnDeathPostfix)));
            Harmony.Patch(onSpawned, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(OnSpawnedPostfix)));
            Harmony.Patch(giveBoost, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(GiveBoostPostfix)));
            Harmony.Patch(takeAll, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(TakeAllPostfix)));
            Harmony.Patch(termInit, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(RegisterCommand)));

            EnsureTemplate();
            PushNumbers();
            Log.LogInfo("[CorpseRun] " + Numbers());
        }

        public override void Disable()
        {
            try
            {
                RemovePull();
                GraveCompassHud.Destroy();
                var odb = ObjectDB.instance;
                if (odb != null && odb.m_StatusEffects != null && _pullTemplate != null)
                    odb.m_StatusEffects.Remove(_pullTemplate);
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] teardown: " + e.Message); }
            base.Disable();
        }

        // ---- status effect registration ---------------------------------------------------

        private static void EnsureTemplate()
        {
            if (_pullTemplate != null) return;
            _pullTemplate = GravePullStatusEffect.Create();
            if (_inst != null) _inst.PushNumbers();
        }

        /// <summary>Postfix for both ObjectDB.Awake and ObjectDB.CopyOtherDB.</summary>
        private static void ObjectDBPostfix(ObjectDB __instance)
        {
            if (_inst == null || !_inst.Active) return;
            try
            {
                Register(__instance);
                if (_inst._selfTest.Value) RunSelfTest(__instance);
            }
            catch (Exception e)
            {
                Log.LogError("[CorpseRun] ObjectDB registration failed: " + e);
            }
        }

        private static void Register(ObjectDB odb)
        {
            if (odb == null || odb.m_StatusEffects == null) return;
            EnsureTemplate();

            // Duplicate guard: hand-rolled so a null entry left by another mod cannot NRE us.
            for (int i = 0; i < odb.m_StatusEffects.Count; i++)
            {
                var se = odb.m_StatusEffects[i];
                if (se != null && se.name == GravePullStatusEffect.SeName)
                {
                    if (!ReferenceEquals(se, _pullTemplate)) { odb.m_StatusEffects[i] = _pullTemplate; break; }
                    return;
                }
            }

            BorrowIcon(odb);
            odb.m_StatusEffects.Add(_pullTemplate);
            Log.LogInfo("[CorpseRun] registered status effect '" + GravePullStatusEffect.SeName +
                        "' (hash " + PullHash + ") in ObjectDB, " + odb.m_StatusEffects.Count +
                        " total" + (_pullTemplate.m_icon != null
                            ? ", icon borrowed from '" + _inst._pullIconFrom.Value + "'"
                            : ", NO ICON (donor not found)"));
        }

        /// <summary>Reuse an existing status effect's sprite - the mod ships no assets.</summary>
        private static void BorrowIcon(ObjectDB odb)
        {
            if (_pullTemplate.m_icon != null) return;
            string want = _inst != null ? _inst._pullIconFrom.Value : "Rested";
            Sprite any = null;
            foreach (var se in odb.m_StatusEffects)
            {
                if (se == null || se.m_icon == null) continue;
                if (any == null) any = se.m_icon;
                if (string.Equals(se.name, want, StringComparison.OrdinalIgnoreCase))
                {
                    _pullTemplate.m_icon = se.m_icon;
                    return;
                }
            }
            _pullTemplate.m_icon = any;
        }

        // ---- death / respawn ----------------------------------------------------------------

        private static void OnDeathPostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                // Player.OnDeath has already called SetDeathPoint (Player.cs:3117) - we ignore it
                // and write our own record instead, stamped with the world we are actually in.
                _diedThisSession = true;
                _looted = false;
                _lastScaled = null;
                _lootDistanceFromHome = -1f;
                GraveRecord.Write(__instance, __instance.transform.position);
                ReadGrave();
                Log.LogInfo("[CorpseRun] death recorded in world '" + GraveRecord.CurrentWorld() + "' at " +
                            (_haveGrave ? _grave.ToString("F0") : "?") +
                            " - respawn grants armed (food=" + _inst._respawnFoodEnabled.Value +
                            " rested=" + _inst._respawnRestedEnabled.Value + ")");
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] OnDeath: " + e.Message); }
        }

        private static void OnSpawnedPostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            // A first spawn / login must never be rewarded: only a death recorded in THIS session
            // arms the grants. PlayerProfile.HaveDeathPoint() alone would be true forever after
            // the first death (nothing in vanilla ever clears it).
            if (!_diedThisSession) return;
            _diedThisSession = false;
            // Deferred by half a second: SEMan.AddStatusEffect only does anything when the
            // player's ZNetView reports IsOwner, which is settled a frame or two after spawn.
            _grantPending = true;
            _grantAt = Time.time + 0.5f;
            ReadGrave();
        }

        /// <summary>
        /// Refresh <see cref="_grave"/> from OUR OWN record in the player's custom data.
        ///
        /// This used to read PlayerProfile.HaveDeathPoint()/GetDeathPoint(), which are written on
        /// every death and never cleared by anything in vanilla - so an old character joining a new
        /// world arrived with the compass already lit, pointing at a death spot from some other
        /// world. The record is world-stamped and is deleted when the grave is looted, so all three
        /// features now switch on only after a death in THIS world and switch off when it is over.
        /// </summary>
        private static void ReadGrave()
        {
            _haveGrave = false;
            _graveWorld = "";
            try
            {
                var me = Player.m_localPlayer;
                if (me == null) return;
                Vector3 pos; double when; string world;
                if (!GraveRecord.TryRead(me, out pos, out when, out world)) return;
                _grave = pos;
                _graveWorld = world ?? "";
                _haveGrave = true;
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] grave record read: " + e.Message); }
        }

        private static Vector3 HomePoint()
        {
            var prof = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (prof == null) return Vector3.zero;
            return prof.HaveCustomSpawnPoint() ? prof.GetCustomSpawnPoint() : prof.GetHomePoint();
        }

        private static void GrantRespawnGifts(Player me)
        {
            var c = _inst;
            var parts = new List<string>();

            if (c._respawnFoodEnabled.Value)
            {
                int want = Mathf.Clamp(c._respawnFoodCount.Value, 0, 3);
                int given = 0;
                foreach (var raw in c._respawnFoods.Value.Split(','))
                {
                    if (given >= want) break;
                    string nm = raw.Trim();
                    if (nm.Length == 0) continue;
                    if (GiveFood(me, nm)) { parts.Add("food:" + nm); given++; }
                }
                if (given < want)
                    Log.LogWarning("[CorpseRun] RespawnFood: wanted " + want + " item(s) from '" +
                                   c._respawnFoods.Value + "', granted " + given +
                                   " (missing prefab, or all 3 food slots were full)");
            }

            if (c._respawnRestedEnabled.Value)
            {
                float secs = c._restedMinutes.Value * 60f;
                var seman = me.GetSEMan();
                if (seman != null && secs > 0f)
                {
                    seman.AddStatusEffect(RestedHash, true);
                    var se = seman.GetStatusEffect(RestedHash);
                    if (se != null)
                    {
                        // AddStatusEffect(..., resetTime:true) has already put m_time back to 0
                        // (SE_Rested.ResetTime -> UpdateTTL), so raising the public m_ttl is all
                        // that is left. Never lower it: a comfy bed may already beat our floor.
                        if (se.m_ttl < secs) se.m_ttl = secs;
                        parts.Add("rested:" + Mathf.RoundToInt(se.m_ttl) + "s");
                    }
                    else
                    {
                        Log.LogWarning("[CorpseRun] RespawnRested: 'Rested' status effect not found " +
                                       "in ObjectDB (hash " + RestedHash + ")");
                    }
                }
            }

            _lastGrantText = parts.Count == 0 ? "none" : string.Join(" ", parts.ToArray());
            Log.LogInfo("[CorpseRun] death-respawn grants: " + _lastGrantText);
        }

        /// <summary>
        /// Add a food to the player's belly exactly the way Player.EatFood does (Player.cs:2183),
        /// but built from the prefab so the item does not have to be in anyone's inventory.
        /// </summary>
        private static bool GiveFood(Player me, string prefabName)
        {
            try
            {
                var odb = ObjectDB.instance;
                if (odb == null) return false;
                var prefab = odb.GetItemPrefab(prefabName);
                if (prefab == null)
                {
                    Log.LogWarning("[CorpseRun] RespawnFood: no item prefab named '" + prefabName + "'");
                    return false;
                }
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return false;
                var shared = drop.m_itemData.m_shared;
                if (shared.m_food <= 0f && shared.m_foodStamina <= 0f && shared.m_foodEitr <= 0f)
                {
                    Log.LogWarning("[CorpseRun] RespawnFood: '" + prefabName + "' is not a food");
                    return false;
                }

                var foods = me.GetFoods();
                if (foods == null) return false;
                foreach (var f in foods)
                    if (f != null && f.m_item != null && f.m_item.m_shared != null &&
                        f.m_item.m_shared.m_name == shared.m_name) return false;   // already eaten
                if (foods.Count >= 3) return false;

                var item = drop.m_itemData.Clone();
                item.m_dropPrefab = prefab;
                item.m_stack = 1;

                var food = new Player.Food();
                food.m_name = prefab.name;
                food.m_item = item;
                food.m_time = shared.m_foodBurnTime;
                food.m_health = shared.m_food;
                food.m_stamina = shared.m_foodStamina;
                food.m_eitr = shared.m_foodEitr;
                foods.Add(food);

                if (_updateFood != null) _updateFood.Invoke(me, new object[] { 0f, true });

                Log.LogInfo("[CorpseRun] RespawnFood: granted '" + prefab.name + "' (hp " +
                            shared.m_food + " / sta " + shared.m_foodStamina + " / eitr " +
                            shared.m_foodEitr + " for " + shared.m_foodBurnTime + "s)");
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[CorpseRun] RespawnFood('" + prefabName + "') failed: " + e.Message);
                return false;
            }
        }

        // ---- tombstone looted -----------------------------------------------------------------

        /// <summary>The grave was emptied on its owner's client - vanilla has just added CorpseRun.</summary>
        private static void GiveBoostPostfix(TombStone __instance) { OnGraveTouched(__instance, "emptied"); }

        /// <summary>The grave was looted with Take All on this client.</summary>
        private static void TakeAllPostfix(TombStone __instance) { OnGraveTouched(__instance, "looted"); }

        private static void OnGraveTouched(TombStone ts, string how)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            try
            {
                if (ts == null || Player.m_localPlayer == null) return;
                if (!_haveGrave) ReadGrave();
                if (!_haveGrave) return;
                float d = Vector3.Distance(ts.transform.position, _grave);
                if (d > _inst._lootMatchDistance.Value) return;   // somebody else's grave

                if (ts.m_lootStatusEffect != null) _corpseRunHash = ts.m_lootStatusEffect.NameHash();
                _lootDistanceFromHome = Vector3.Distance(_grave, HomePoint());

                _looted = true;
                _haveGrave = false;              // the record is gone; nothing may re-arm off it
                GraveRecord.Clear(Player.m_localPlayer);
                GraveCompassHud.Hide();
                RemovePull();

                if (_inst._scaledEnabled.Value)
                {
                    // The SE may land a frame or two later (SEMan.AddStatusEffect on the owner),
                    // so scaling is retried from the tick for a few seconds instead of right here.
                    _scalePending = true;
                    _scalePendingUntil = Time.time + 5f;
                }
                Log.LogInfo("[CorpseRun] grave " + how + " " + d.ToString("0.0") + "m from the " +
                            "recorded death point - compass off, pull off" +
                            (_inst._scaledEnabled.Value ? ", CorpseRun scaling armed" : ""));
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] grave " + how + ": " + e.Message); }
        }

        /// <summary>
        /// nvlb.grave.clear - forget the recorded grave by hand. The escape hatch for a grave that
        /// can no longer be reached or looted (destroyed, unreachable terrain, another mod ate it),
        /// which would otherwise keep the compass and GravePull on until the next death.
        /// </summary>
        private static void RegisterCommand()
        {
            if (_commandRegistered) return;
            _commandRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.grave.clear",
                    "Forget the recorded grave: turns the Grave Compass and Grave Pull off until your next death.",
                    new Terminal.ConsoleEvent(ClearCommand));
                Log.LogInfo("[CorpseRun] console command 'nvlb.grave.clear' registered");
            }
            catch (Exception e)
            {
                _commandRegistered = false;
                Log.LogError("[CorpseRun] could not register nvlb.grave.clear: " + e);
            }
        }

        private static void ClearCommand(Terminal.ConsoleEventArgs args)
        {
            var me = Player.m_localPlayer;
            string had = GraveRecord.Describe(me);
            bool cleared = GraveRecord.Clear(me);
            _haveGrave = false;
            _graveWorld = "";
            _lastDistance = -1f;
            _lootDistanceFromHome = -1f;
            GraveCompassHud.Hide();
            RemovePull();
            string msg = cleared ? "grave record cleared (was " + had + ")" : "no grave record to clear";
            if (args != null && args.Context != null) args.Context.AddString("[CorpseRun] " + msg);
            Log.LogInfo("[CorpseRun] " + msg);
        }

        private static int CorpseRunHash()
        {
            if (_corpseRunHash == 0) _corpseRunHash = "CorpseRun".GetStableHashCode();
            return _corpseRunHash;
        }

        private static bool TryScaleCorpseRun(Player me)
        {
            var seman = me.GetSEMan();
            if (seman == null) return false;
            var live = seman.GetStatusEffect(CorpseRunHash()) as SE_Stats;
            if (live == null) return false;
            if (ReferenceEquals(live, _lastScaled)) return true;

            // The live effect is SEMan's own Clone() (SEMan:196), never the ObjectDB template,
            // so mutating it here cannot leak into anybody else's buff.
            var template = ObjectDB.instance != null
                ? ObjectDB.instance.GetStatusEffect(CorpseRunHash()) as SE_Stats
                : null;
            float baseTtl = template != null && template.m_ttl > 0f ? template.m_ttl : live.m_ttl;
            float baseRegen = template != null ? template.m_staminaRegenMultiplier
                                               : live.m_staminaRegenMultiplier;

            float dist = _lootDistanceFromHome >= 0f
                ? _lootDistanceFromHome
                : (_haveGrave ? Vector3.Distance(_grave, HomePoint()) : 0f);
            float ttl = ScaledDuration(baseTtl, dist, _inst._scaledDurationPer100m.Value,
                                       _inst._scaledMaxDurationSec.Value);
            live.m_ttl = ttl;
            live.ResetTime();

            float extra = 0f;
            if (_inst._scaledExtraRegen.Value != 0f && _inst._scaledRegenFullDistance.Value > 0f)
            {
                float s = Mathf.Clamp01(dist / _inst._scaledRegenFullDistance.Value);
                extra = _inst._scaledExtraRegen.Value * s;
                live.m_staminaRegenMultiplier = baseRegen + extra;
            }

            _lastScaled = live;
            _lastScaledTtl = ttl;
            Log.LogInfo("[CorpseRun] CorpseRunScaled: grave was " + Mathf.RoundToInt(dist) +
                        "m from home -> duration " + baseTtl.ToString("0") + "s -> " +
                        ttl.ToString("0") + "s, staminaRegen x" +
                        live.m_staminaRegenMultiplier.ToString("0.00") +
                        " (base " + baseRegen.ToString("0.00") + " + " + extra.ToString("0.00") + ")");
            return true;
        }

        // ---- the tick ---------------------------------------------------------------------------

        private static void PlayerUpdatePostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            try { Tick(__instance, Time.deltaTime); }
            catch (Exception e) { Log.LogWarning("[CorpseRun] tick: " + e.Message); }
        }

        private static void Tick(Player me, float dt)
        {
            var c = _inst;

            if (_grantPending && Time.time >= _grantAt)
            {
                _grantPending = false;
                GrantRespawnGifts(me);
            }

            if (_scalePending)
            {
                if (TryScaleCorpseRun(me) || Time.time > _scalePendingUntil) _scalePending = false;
            }

            if (me.IsDead()) { GraveCompassHud.Hide(); return; }

            if (!_haveGrave) ReadGrave();
            float dist = -1f;
            if (_haveGrave && !_looted)
            {
                dist = Vector3.Distance(me.transform.position, _grave);
                _lastDistance = dist;
            }
            else
            {
                _lastDistance = -1f;
            }

            // --- compass ---
            // An edge waypoint has to follow the camera, so it is refreshed every frame; the
            // CompassUpdateSec throttle still governs the fixed-position mode, where nothing moves
            // between ticks anyway. Show() is a projection and a few assignments - no allocation.
            bool wantCompass = c._compassEnabled.Value && dist >= 0f && dist > c._compassHideDistance.Value;
            _compassAcc += dt;
            float cEvery = c._compassUpdateSec.Value < 0.05f ? 0.05f : c._compassUpdateSec.Value;
            bool due = _compassAcc >= cEvery;
            if (due) _compassAcc = 0f;

            // --- hold-to-dismiss (must run before Show, it writes the hint line) ---
            UpdateClearHold(me, dt, wantCompass);

            if (GraveCompassHud.EdgeMode || due)
            {
                if (wantCompass) GraveCompassHud.Show(me, _grave, dist);
                else GraveCompassHud.Hide();
            }

            // --- grave pull ---
            _pullAcc += dt;
            float pEvery = c._pullUpdateSec.Value < 0.1f ? 0.1f : c._pullUpdateSec.Value;
            if (_pullAcc >= pEvery)
            {
                _pullAcc = 0f;
                UpdatePull(me, dist);
            }
        }

        /// <summary>
        /// HOLD ClearGraveKey (default Delete) for ClearGraveHoldSec to dismiss the grave marker and
        /// Grave Pull - the console-free version of nvlb.grave.clear, because a grave you can no
        /// longer reach otherwise follows you around until you die again.
        ///
        /// It is a HOLD, not a press, so a stray key cannot cost you your grave, and it is gated on
        /// the same chat/console/menu/map checks as the DualPowers hotkey (ZInput.GetKey reads the
        /// raw device and knows nothing about the UI on its own). The compass carries the prompt and
        /// the progress, so the feature is discoverable exactly when it is useful.
        /// </summary>
        private static void UpdateClearHold(Player me, float dt, bool compassShown)
        {
            var c = _inst;
            if (!compassShown || DismissKeyLabel().Length == 0 || !_haveGrave || _looted)
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint("");
                return;
            }

            if (!DualPowersModule.InputAllowed(me))
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint("Hold " + DismissKeyLabel() + " to dismiss grave");
                return;
            }

            float need = c._clearGraveHoldSec.Value < 0.1f ? 0.1f : c._clearGraveHoldSec.Value;
            if (NvlbKeys.Held("GraveDismiss"))
            {
                _clearHeld += dt;
                if (_clearHeld >= need)
                {
                    _clearHeld = 0f;
                    GraveCompassHud.SetHint("");
                    ClearCommand(null);                       // the same path as nvlb.grave.clear
                    me.Message(MessageHud.MessageType.Center, "Grave marker cleared");
                    return;
                }
                GraveCompassHud.SetHint("Dismissing grave... " +
                                        Mathf.RoundToInt(Mathf.Clamp01(_clearHeld / need) * 100f) + "%");
            }
            else
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint("Hold " + DismissKeyLabel() + " to dismiss grave");
            }
        }

        private static void UpdatePull(Player me, float dist)
        {
            var c = _inst;
            float s = (c._pullEnabled.Value && dist >= 0f)
                ? PullStrength(dist, c._pullMinDistance.Value, c._pullFullDistance.Value)
                : 0f;

            _lastStrength = s;
            GravePullStatusEffect.Strength = s;
            GravePullStatusEffect.Distance = dist < 0f ? 0f : dist;

            var seman = me.GetSEMan();
            if (seman == null) return;
            var live = seman.GetStatusEffect(PullHash);

            if (s > 0f)
            {
                if (live == null)
                {
                    EnsureTemplate();
                    if (ObjectDB.instance != null) Register(ObjectDB.instance);
                    seman.AddStatusEffect(_pullTemplate, false);
                    _pullOn = true;
                    Log.LogInfo("[CorpseRun] GravePull ON (" + Mathf.RoundToInt(dist) + "m out, " +
                                Mathf.RoundToInt(s * 100f) + "% strength)");
                }
                else
                {
                    live.ResetTime();
                    _pullOn = true;
                }
            }
            else if (live != null)
            {
                seman.RemoveStatusEffect(PullHash, true);
                _pullOn = false;
                Log.LogInfo("[CorpseRun] GravePull OFF");
            }
            else
            {
                _pullOn = false;
            }
        }

        private static void RemovePull()
        {
            try
            {
                GravePullStatusEffect.Strength = 0f;
                var me = Player.m_localPlayer;
                if (me == null) return;
                var seman = me.GetSEMan();
                if (seman != null && seman.HaveStatusEffect(PullHash))
                    seman.RemoveStatusEffect(PullHash, true);
                _pullOn = false;
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] pull removal failed: " + e.Message); }
        }

        // ---- pure maths (unit-tested by the self test) ---------------------------------------

        /// <summary>s = clamp((dist - min) / full, 0, 1).</summary>
        internal static float PullStrength(float dist, float min, float full)
        {
            if (full <= 0f) return dist > min ? 1f : 0f;
            return Mathf.Clamp01((dist - min) / full);
        }

        /// <summary>base x (1 + per100m x dist/100), capped.</summary>
        internal static float ScaledDuration(float baseTtl, float dist, float per100m, float cap)
        {
            float ttl = baseTtl * (1f + per100m * (dist / 100f));
            if (cap > 0f && ttl > cap) ttl = cap;
            return ttl;
        }

        // ---- nvlb.status ----------------------------------------------------------------------

        public override string StatusDetail()
        {
            string grave = _lastDistance >= 0f
                ? _lastDistance.ToString("0") + "m"
                : (_looted ? "looted" : (_haveGrave ? "<hide" : "none"));
            string compass = !_compassEnabled.Value ? "off"
                : GraveCompassHud.Failed ? "FAILED"
                : GraveCompassHud.Visible ? "shown" : "hidden";
            return "grave=" + grave + " compass=" + compass +
                   " pull=" + (_pullOn ? Mathf.RoundToInt(_lastStrength * 100f) + "%" : "off") +
                   " lastGrants=" + _lastGrantText +
                   " lastCorpseRun=" + (_lastScaledTtl > 0f ? _lastScaledTtl.ToString("0") + "s" : "-") +
                   " diedThisSession=" + _diedThisSession +
                   " record=" + GraveRecord.Describe(Player.m_localPlayer);
        }

        // ---- self test (headless, 0 players) ----------------------------------------------------

        private static void RunSelfTest(ObjectDB odb)
        {
            if (_selfTestDone) return;

            // ObjectDB.Awake fires first on the bootstrap DB, which holds only whatever other
            // plugins have injected - no vanilla items and no vanilla status effects. Wait for the
            // real one (it arrives through CopyOtherDB) or the whole test measures nothing.
            if (odb.GetStatusEffect(RestedHash) == null || odb.m_items == null || odb.m_items.Count < 50)
            {
                Log.LogInfo("[CorpseRun] SelfTest: skipping the bootstrap ObjectDB (" +
                            (odb.m_items == null ? 0 : odb.m_items.Count) + " items, " +
                            (odb.m_StatusEffects == null ? 0 : odb.m_StatusEffects.Count) +
                            " status effects, no vanilla 'Rested') - waiting for the populated DB");
                return;
            }

            _selfTestDone = true;
            var c = _inst;
            Log.LogInfo("[CorpseRun] SelfTest: --- begin ---");

            // (1) top-10 stamina foods in ObjectDB.
            var foods = new List<KeyValuePair<string, ItemDrop.ItemData.SharedData>>();
            if (odb.m_items != null)
            {
                foreach (var go in odb.m_items)
                {
                    if (go == null) continue;
                    var drop = go.GetComponent<ItemDrop>();
                    if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;
                    var sh = drop.m_itemData.m_shared;
                    if (sh.m_foodStamina <= 0f) continue;
                    foods.Add(new KeyValuePair<string, ItemDrop.ItemData.SharedData>(go.name, sh));
                }
            }
            foods.Sort((a, b) => b.Value.m_foodStamina.CompareTo(a.Value.m_foodStamina));
            Log.LogInfo("[CorpseRun] SelfTest: " + foods.Count + " items in ObjectDB with m_foodStamina > 0; top 10:");
            for (int i = 0; i < foods.Count && i < 10; i++)
            {
                var f = foods[i];
                Log.LogInfo("[CorpseRun] SelfTest:   " + (i + 1) + ". " + f.Key +
                            "  stamina=" + f.Value.m_foodStamina +
                            " health=" + f.Value.m_food +
                            " eitr=" + f.Value.m_foodEitr +
                            " burn=" + f.Value.m_foodBurnTime + "s" +
                            " regen=" + f.Value.m_foodRegen);
            }
            foreach (var raw in c._respawnFoods.Value.Split(','))
            {
                string nm = raw.Trim();
                if (nm.Length == 0) continue;
                var p = odb.GetItemPrefab(nm);
                var sh = p != null ? p.GetComponent<ItemDrop>() : null;
                Log.LogInfo("[CorpseRun] SelfTest: default RespawnFoods entry '" + nm + "' -> " +
                            (sh != null && sh.m_itemData != null && sh.m_itemData.m_shared != null
                                ? "PRESENT stamina=" + sh.m_itemData.m_shared.m_foodStamina +
                                  " health=" + sh.m_itemData.m_shared.m_food +
                                  " burn=" + sh.m_itemData.m_shared.m_foodBurnTime + "s  OK"
                                : "MISSING"));
            }

            // (2) the vanilla status effects we lean on.
            DumpVanillaSe(odb, "Rested");
            DumpVanillaSe(odb, "CorpseRun");

            // (3) our own registration, read back through the game's own lookup.
            var found = odb.GetStatusEffect(PullHash);
            Log.LogInfo("[CorpseRun] SelfTest: ObjectDB.GetStatusEffect(\"" + GravePullStatusEffect.SeName +
                        "\".GetStableHashCode()=" + PullHash + ") -> " +
                        (found == null ? "NOT FOUND" :
                            "'" + found.name + "' / m_name='" + found.m_name + "' ttl=" + found.m_ttl +
                            " icon=" + (found.m_icon != null ? "yes" : "no") +
                            (ReferenceEquals(found, _pullTemplate) ? " SAME INSTANCE  OK" : " DIFFERENT INSTANCE")) +
                        ", is GravePullStatusEffect=" + (found is GravePullStatusEffect) +
                        ", list size=" + odb.m_StatusEffects.Count);

            // (4) the maths, at the four distances from the spec.
            float min = c._pullMinDistance.Value, full = c._pullFullDistance.Value;
            float per = c._scaledDurationPer100m.Value, cap = c._scaledMaxDurationSec.Value;
            var baseSe = odb.GetStatusEffect("CorpseRun".GetStableHashCode()) as SE_Stats;
            float baseTtl = baseSe != null && baseSe.m_ttl > 0f ? baseSe.m_ttl : 50f;
            Log.LogInfo("[CorpseRun] SelfTest: maths with PullMin=" + min + " PullFull=" + full +
                        " Per100m=" + per + " Cap=" + cap + " baseCorpseRunTtl=" + baseTtl + "s");
            foreach (float d in new[] { 0f, 100f, 500f, 2000f })
            {
                float s = PullStrength(d, min, full);
                float regen = 1f + c._pullMaxRegenBonus.Value * s;
                float costMul = 1f - c._pullMaxDrainReduction.Value * s;
                float ttl = ScaledDuration(baseTtl, d, per, cap);
                float es = Mathf.Clamp01(d / c._scaledRegenFullDistance.Value);
                Log.LogInfo("[CorpseRun] SelfTest:   d=" + d.ToString("0") + "m  pull s=" +
                            s.ToString("0.000") + " -> staminaRegen x" + regen.ToString("0.00") +
                            ", run/jump cost x" + costMul.ToString("0.00") +
                            " | CorpseRun ttl " + ttl.ToString("0.0") + "s" +
                            (cap > 0f && ttl >= cap ? " (CAPPED)" : "") +
                            ", extraRegen +" + (c._scaledExtraRegen.Value * es).ToString("0.00"));
            }

            GraveRecordSelfTest();

            Log.LogInfo("[CorpseRun] SelfTest: --- end ---");
        }

        /// <summary>
        /// The four cases from the 0.4.2 bug report, driven straight through GraveRecord's
        /// dictionary-level API - no Player, no world, so it runs on a headless server with zero
        /// players. Case 1 is the actual bug: a character whose .fch has had a death point since
        /// forever, joining a world it has never died in, must get NOTHING.
        /// </summary>
        private static void GraveRecordSelfTest()
        {
            int pass = 0, fail = 0;
            Action<bool, string> check = (ok, what) =>
            {
                if (ok) { pass++; Log.LogInfo("[CorpseRun] SelfTest: PASS  " + what); }
                else { fail++; Log.LogError("[CorpseRun] SelfTest: FAIL  " + what); }
            };

            const string here = "NEWTEST";
            const string other = "BLACKWORLD";
            var pos = new Vector3(1234.5f, -12.25f, -678.75f);
            var data = new Dictionary<string, string>();
            Vector3 got; double when; string world;

            // (1) stale PlayerProfile death point, no record of ours -> everything inactive.
            check(!GraveRecord.TryRead(data, here, out got, out when, out world),
                  "(1) a character with a stale profile death point but no nvlb.grave record reads as NO grave " +
                  "- compass, GravePull and CorpseRunScaled all stay off");

            // (2) a death in this world -> active, and the position survives the round trip.
            GraveRecord.Write(data, here, pos, 4242.5d);
            bool ok2 = GraveRecord.TryRead(data, here, out got, out when, out world);
            check(ok2 && (got - pos).sqrMagnitude < 0.0001f && world == here && Math.Abs(when - 4242.5d) < 0.001d,
                  "(2) a record written in '" + here + "' reads back there: " + got.ToString("F2") +
                  " world='" + world + "' t=" + when);

            // (3) the same record, read from a different world -> inactive, and NOT destroyed.
            check(!GraveRecord.TryRead(data, other, out got, out when, out world),
                  "(3) the same record is invisible in '" + other + "' (the 0.4.2 bug: an old grave from " +
                  "another world used to light the compass)");
            check(GraveRecord.Has(data),
                  "(3b) ...and it is left in place, so returning to '" + here + "' still finds the grave");

            // (4) looting the grave clears it.
            check(GraveRecord.Clear(data) && !GraveRecord.Has(data) &&
                  !GraveRecord.TryRead(data, here, out got, out when, out world),
                  "(4) looting/emptying the grave clears the record - no grave anywhere afterwards");

            // A corrupt or truncated record must degrade to "no grave", never throw.
            var junk = new Dictionary<string, string> { { GraveRecord.Key, "1|OnlyTwo" } };
            check(!GraveRecord.TryRead(junk, here, out got, out when, out world),
                  "(5) a corrupt record decodes to no grave instead of throwing");

            // A world name containing our separator must survive.
            var odd = new Dictionary<string, string>();
            GraveRecord.Write(odd, "a|b", pos, 1d);
            check(GraveRecord.TryRead(odd, "a|b", out got, out when, out world) && world == "a|b",
                  "(6) a world name containing '|' round trips");

            Log.LogInfo("[CorpseRun] SelfTest: grave record - " + pass + " passed, " + fail + " FAILED");
        }

        private static void DumpVanillaSe(ObjectDB odb, string name)
        {
            var se = odb.GetStatusEffect(name.GetStableHashCode());
            if (se == null)
            {
                Log.LogWarning("[CorpseRun] SelfTest: vanilla status effect '" + name + "' NOT FOUND in ObjectDB");
                return;
            }
            string s = "[CorpseRun] SelfTest: vanilla '" + se.name + "' (" + se.GetType().Name +
                       ") m_name='" + se.m_name + "' ttl=" + se.m_ttl +
                       " icon=" + (se.m_icon != null ? "yes" : "no");
            var st = se as SE_Stats;
            if (st != null)
                s += " staminaRegenMul=" + st.m_staminaRegenMultiplier +
                     " runStaminaDrainMod=" + st.m_runStaminaDrainModifier +
                     " jumpStaminaUseMod=" + st.m_jumpStaminaUseModifier +
                     " addMaxCarryWeight=" + st.m_addMaxCarryWeight;
            var rested = se as SE_Rested;
            if (rested != null)
                s += " baseTTL=" + rested.m_baseTTL + " ttlPerComfort=" + rested.m_TTLPerComfortLevel;
            Log.LogInfo(s);
        }
    }
}
