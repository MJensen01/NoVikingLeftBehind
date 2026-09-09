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
    ///  |                                 |         | (SetDeathPoint has already run, Player:3431)  |
    ///  | Player.CreateTombStone          | prefix  | item stacks we still had a moment before      |
    ///  |                                 |         | vanilla moved them into the grave, so a death |
    ///  |                                 |         | that spawns NO tombstone records nothing      |
    ///  | TombStone.Setup(string,long)    | postfix | the grave just built: its real position, its  |
    ///  |                                 |         | ZDOID and its item count, read off its own    |
    ///  |                                 |         | Container after MoveInventoryToGrave (:152)   |
    ///  | Player.OnSpawned(bool)          | postfix | arm the respawn grants (Game.SpawnPlayer:436) |
    ///  | TombStone.GiveBoost             | postfix | grave emptied on its owner's client: the      |
    ///  |                                 |         | vanilla CorpseRun SE was just added (:206)    |
    ///  | TombStone.OnTakeAllSuccess      | postfix | grave looted on the looter's client (:112)    |
    ///
    /// MULTI-GRAVE (0.9.1). Dying on the run back used to overwrite the record, so a grave holding
    /// two mushrooms hid the 30-item grave you were actually going to. <see cref="GraveRecord"/>
    /// now keeps up to five unlooted graves per world; this module TRACKS the one with the most
    /// items (ties to the most recent), falls through to the next best when that one is looted or
    /// dismissed, and lets you cycle by hand with [CorpseRun] CycleGraveKey or nvlb.grave.next.
    /// Everything downstream - compass, GravePull, CorpseRunScaled, hold-to-dismiss - points at
    /// the tracked grave and is otherwise exactly as it was.
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
        private ConfigEntry<string> _cycleGraveKey;
        private ConfigEntry<int> _minItemsToTrack;

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

        /// <summary>Every grave recorded in THIS world, ranked (most items first, ties to newest).</summary>
        private static List<Grave> _graves = new List<Grave>();
        /// <summary>The one everything points at. Null when there is nothing to point at.</summary>
        private static Grave _tracked;
        /// <summary>Which grave the player last selected by hand, held across the per-frame re-read.
        /// Session-only on purpose: after a relog the "most items" rule takes over again.</summary>
        private static string _trackedId;
        /// <summary>The raw custom-data string <see cref="_graves"/> was decoded from, so the tick
        /// does not re-parse it every frame. null = "we have not decoded anything yet". The world
        /// is part of the key because the decoded list is already filtered to one world - the same
        /// record string means a different list of graves in a different world.</summary>
        private static string _cacheRaw;
        private static string _cacheWorld;
        private static bool _cacheValid;

        // The tombstone vanilla built for our own death, handed from the TombStone.Setup postfix to
        // the Player.OnDeath postfix a few statements later (Player.CreateTombStone:3306-3308).
        private static bool _pendingTomb;
        private static Vector3 _pendingTombPos;
        private static int _pendingTombItems;
        private static long _pendingZdoUser;
        private static uint _pendingZdoId;
        private static float _pendingAt = -999f;
        /// <summary>Item stacks the player still held when CreateTombStone started. 0 means vanilla
        /// spawns no tombstone at all, so there is nothing worth remembering.</summary>
        private static int _preDeathItems = -1;
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
        private static KeyCode _cycleKey = KeyCode.None;
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
                Opt.T("Which foods to grant on a death-respawn, in order")
                    .Pick(new PickerSpec(PickerSource.Foods)));
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

            _cycleGraveKey = BindLocal("CycleGraveKey", "None",
                "Machine-local. PRESS this key to point the grave compass at the next recorded " +
                "grave - the same thing nvlb.grave.next does. Only useful when you have died more " +
                "than once without looting; the compass otherwise picks the grave with the most " +
                "items in it on its own. A UnityEngine.KeyCode name; 'None' (the default) leaves " +
                "it unbound, and it can be given a key on Valheim's own Keyboard & Mouse page. " +
                "Ignored while a menu, the map, chat or the console has your input.",
                Opt.T("Key pressed to point the compass at the next grave"));

            _minItemsToTrack = BindSynced("MinItemsToTrack", 1,
                "How many item stacks a grave must hold before it is remembered at all. The point " +
                "of the multi-grave tracker is that dying with almost nothing on you must never " +
                "hide the grave that holds your gear, so raise this to ignore trivial deaths " +
                "entirely: at 5, a grave with 4 stacks in it is never recorded and the compass " +
                "keeps pointing at the real one. A death that spawns no tombstone at all (empty " +
                "inventory) is never recorded whatever this says.",
                Opt.N("Item stacks a grave needs before it is tracked", 1, 32));

            _lootMatchDistance = BindSynced("LootMatchDistance", 20f,
                "How close a tombstone must be to a recorded death point of yours to count as THAT " +
                "grave when it is looted or emptied. Guards against another player's grave " +
                "clearing your compass. The nearest of your own records inside this radius wins, " +
                "and an exact tombstone match beats distance outright, so looting one of several " +
                "graves only ever removes the right one.",
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
            _clearKey = ParseKey(_clearGraveKey.Value, "ClearGraveKey");
            _cycleKey = ParseKey(_cycleGraveKey.Value, "CycleGraveKey");

            // 0.8.1: a real, rebindable Valheim keybinding, so Delete can be moved on the game's
            // own Keyboard & Mouse page rather than only in a cfg file. 0.9.1 adds the cycle key
            // the same way - a default of KeyCode.None still registers the row, so it shows up on
            // the page as an unbound action waiting for a key.
            NvlbKeys.Declare("GraveDismiss", "Dismiss grave (hold)", delegate { return _clearKey; });
            NvlbKeys.Declare("GraveCycle", "Next grave", delegate { return _cycleKey; });
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

        /// <summary>Same, for the 0.9.1 "next grave" key. "" when nothing is bound to it.</summary>
        private static string CycleKeyLabel()
        {
            var s = NvlbKeys.Label("GraveCycle");
            if (!string.IsNullOrEmpty(s)) return s;
            return _cycleKey == KeyCode.None ? "" : _cycleKey.ToString();
        }

        /// <summary>KeyCode name -> KeyCode; an unparsable name disables the hotkey rather than throwing.</summary>
        private static KeyCode ParseKey(string s, string setting)
        {
            if (string.IsNullOrEmpty(s)) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); }
            catch
            {
                Log.LogWarning("[CorpseRun] " + setting + " = '" + s + "' is not a UnityEngine.KeyCode " +
                               "name - that hotkey is off.");
                return KeyCode.None;
            }
        }

        private string Numbers()
        {
            return "compass=" + _compassEnabled.Value + "(" + _compassMode.Value + " margin " +
                   _compassEdgeMargin.Value + "px, hide<" + _compassHideDistance.Value +
                   "m every " + _compassUpdateSec.Value + "s, dismiss=hold " + DismissKeyLabel() + " " +
                   _clearGraveHoldSec.Value + "s, cycle=" +
                   (CycleKeyLabel().Length == 0 ? "unbound" : CycleKeyLabel()) +
                   ", hint alpha " + _hintAlpha.Value + " scale " +
                   _hintScale.Value + ")" +
                   " graves=max " + GraveRecord.MaxPerWorld + "/world min " + _minItemsToTrack.Value + " items" +
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
            // 0.9.1: the two hooks that tell us HOW MUCH was in the grave, which is what decides
            // which of several graves the compass points at.
            var createTomb = AccessTools.Method(typeof(Player), "CreateTombStone");
            if (createTomb == null) throw new Exception("Player.CreateTombStone() not found");
            var tombSetup = AccessTools.Method(typeof(TombStone), "Setup",
                                               new[] { typeof(string), typeof(long) });
            if (tombSetup == null) throw new Exception("TombStone.Setup(string,long) not found");

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
            Harmony.Patch(createTomb, prefix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(CreateTombStonePrefix)));
            Harmony.Patch(tombSetup, postfix: new HarmonyMethod(typeof(CorpseRunPlusModule), nameof(TombStoneSetupPostfix)));
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
                GraveCompassHud.SetItems(-1);
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

        /// <summary>
        /// Vanilla only builds a tombstone at all when the inventory is not empty
        /// (Player.CreateTombStone:3290), and by the time OnDeath's postfix runs the items have
        /// already been moved out of the player (Inventory.MoveInventoryToGrave:1099). So the count
        /// is taken HERE, one statement before the move, and used as the fallback answer when the
        /// TombStone.Setup hook does not fire. 0 means "no grave will exist" - the whole reason a
        /// death with nothing on you must not become a compass target.
        /// </summary>
        private static void CreateTombStonePrefix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                _pendingTomb = false;
                var inv = __instance.GetInventory();
                _preDeathItems = inv != null ? inv.NrOfItems() : -1;
            }
            catch (Exception e)
            {
                _preDeathItems = -1;
                Log.LogWarning("[CorpseRun] pre-death item count: " + e.Message);
            }
        }

        /// <summary>
        /// The tombstone vanilla just made for us. Setup() is the last thing CreateTombStone does
        /// (Player.cs:3308), by which point MoveInventoryToGrave has filled the container, so this
        /// is the one place the real item count, the real stone position and its ZDOID are all
        /// available at once. Handed to the OnDeath postfix a few statements later.
        ///
        /// Setup only ever runs on the client that created the stone, and the owner id is checked
        /// anyway, so another player's grave can never land in our record.
        /// </summary>
        private static void TombStoneSetupPostfix(TombStone __instance, long ownerUID)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            try
            {
                if (__instance == null || Game.instance == null) return;
                var prof = Game.instance.GetPlayerProfile();
                if (prof == null || prof.GetPlayerID() != ownerUID) return;

                var container = __instance.GetComponent<Container>();
                var inv = container != null ? container.GetInventory() : null;

                _pendingTombPos = __instance.transform.position;
                _pendingTombItems = inv != null ? inv.NrOfItems() : -1;
                _pendingZdoUser = 0L;
                _pendingZdoId = 0u;
                var nview = __instance.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    var zdo = nview.GetZDO();
                    if (zdo != null) { _pendingZdoUser = zdo.m_uid.UserID; _pendingZdoId = zdo.m_uid.ID; }
                }
                _pendingTomb = true;
                _pendingAt = Time.time;
            }
            catch (Exception e)
            {
                _pendingTomb = false;
                Log.LogWarning("[CorpseRun] tombstone hand-off: " + e.Message);
            }
        }

        private static void OnDeathPostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                // Player.OnDeath has already called SetDeathPoint (Player.cs:3431) - we ignore it
                // and write our own record instead, stamped with the world we are actually in.
                _diedThisSession = true;
                _lastScaled = null;
                _lootDistanceFromHome = -1f;
                RecordDeath(__instance);
                Log.LogInfo("[CorpseRun] respawn grants armed (food=" + _inst._respawnFoodEnabled.Value +
                            " rested=" + _inst._respawnRestedEnabled.Value + ")");
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] OnDeath: " + e.Message); }
            finally { _pendingTomb = false; _preDeathItems = -1; }
        }

        /// <summary>
        /// Add this death to the record - or deliberately not.
        ///
        /// Three ways it records nothing, all of them the point of the 0.9.1 fix:
        ///  * vanilla spawned no tombstone (empty inventory, or a DeathKeep* global key), so there
        ///    is no grave in the world to walk to;
        ///  * the grave holds fewer than [CorpseRun] MinItemsToTrack stacks - the "I died again
        ///    with basically nothing on me" death that used to hide the grave holding the gear;
        ///  * we are already holding this world's five graves AND this one is the oldest.
        ///
        /// The position stored is the TOMBSTONE's, not the player's, so the compass points at the
        /// thing you are actually walking to and loot matching lines up with it.
        /// </summary>
        private static void RecordDeath(Player me)
        {
            // The hand-off is only trusted inside the same death: TombStone.Setup runs three
            // statements before this in Player.OnDeath, so anything older is somebody else's stone
            // (or a stone another mod built) and is ignored.
            bool fresh = _pendingTomb && Time.time - _pendingAt < 2f;
            int items = fresh ? _pendingTombItems : (_preDeathItems == 0 ? 0 : -1);
            Vector3 pos = fresh ? _pendingTombPos : me.transform.position;
            long zu = fresh ? _pendingZdoUser : 0L;
            uint zi = fresh ? _pendingZdoId : 0u;
            int min = _inst._minItemsToTrack.Value;

            if (items == 0)
            {
                Log.LogInfo("[CorpseRun] death in '" + GraveRecord.CurrentWorld() + "' with nothing " +
                            "to lose - no tombstone, nothing recorded" +
                            (_graves.Count > 0 ? "; still tracking " + TrackedText() : ""));
                RefreshGraves(true);
                return;
            }

            Grave added; List<Grave> evicted;
            if (!GraveRecord.Add(me, pos, items, zu, zi, min, out added, out evicted))
            {
                Log.LogInfo("[CorpseRun] grave NOT recorded: " + items + " item(s) is below " +
                            "MinItemsToTrack=" + min + " - the compass stays on " + TrackedText());
                RefreshGraves(true);
                return;
            }

            _looted = false;
            RefreshGraves(true);
            foreach (var g in evicted)
                Log.LogInfo("[CorpseRun] grave forgotten (only " + GraveRecord.MaxPerWorld +
                            " are kept per world, oldest out): " + g.Describe());

            int rank = IndexOf(added);
            Log.LogInfo("[CorpseRun] grave recorded #" + rank + " (" + added.ItemsText + ") at (" +
                        Mathf.RoundToInt(added.Pos.x) + "," + Mathf.RoundToInt(added.Pos.z) +
                        "); tracking " + TrackedText() + " of " + _graves.Count);
        }

        /// <summary>"#1 (31 items)" for the tracked grave, for the log and the console.</summary>
        private static string TrackedText()
        {
            if (_tracked == null) return "nothing";
            return "#" + IndexOf(_tracked) + " (" + _tracked.ItemsText + ")";
        }

        /// <summary>1-based rank of a grave in <see cref="_graves"/>, 0 when it is not in there.</summary>
        private static int IndexOf(Grave g)
        {
            if (g == null) return 0;
            for (int i = 0; i < _graves.Count; i++)
                if (string.Equals(_graves[i].Id, g.Id, StringComparison.Ordinal)) return i + 1;
            return 0;
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
            RefreshGraves();
        }

        /// <summary>
        /// Refresh <see cref="_graves"/> and <see cref="_tracked"/> from OUR OWN record in the
        /// player's custom data.
        ///
        /// This used to read PlayerProfile.HaveDeathPoint()/GetDeathPoint(), which are written on
        /// every death and never cleared by anything in vanilla - so an old character joining a new
        /// world arrived with the compass already lit, pointing at a death spot from some other
        /// world. The record is world-stamped and an entry is deleted when its own grave is looted,
        /// so all three features switch on only after a death in THIS world and switch off when it
        /// is over.
        ///
        /// WHICH grave is tracked: the one the player last picked by hand if it is still there,
        /// otherwise the one with the most items in it (ties to the most recent death). So a second
        /// death carrying two mushrooms never steals the compass from a 30-item grave, and looting
        /// the tracked grave drops tracking onto the next best rather than switching everything off.
        ///
        /// The tick calls this every frame while there is no grave, so the decoded list is cached
        /// against the raw custom-data string and only re-parsed when that string actually changes.
        /// </summary>
        private static void RefreshGraves(bool force = false)
        {
            try
            {
                var me = Player.m_localPlayer;
                if (me == null)
                {
                    _graves = new List<Grave>(); _tracked = null; _haveGrave = false;
                    _graveWorld = ""; _cacheValid = false;
                    return;
                }

                string raw = GraveRecord.Raw(me);
                string world = GraveRecord.CurrentWorld();
                if (force || !_cacheValid ||
                    !string.Equals(raw, _cacheRaw, StringComparison.Ordinal) ||
                    !string.Equals(world, _cacheWorld, StringComparison.Ordinal))
                {
                    _graves = GraveRecord.ReadWorld(me);
                    _cacheRaw = raw;
                    _cacheWorld = world;
                    _cacheValid = true;
                }

                Grave t = GraveRecord.ById(_graves, _trackedId) ?? GraveRecord.Best(_graves);
                _tracked = t;
                _trackedId = t != null ? t.Id : null;
                _haveGrave = t != null;
                _grave = t != null ? t.Pos : Vector3.zero;
                _graveWorld = t != null ? t.World : "";

                // "Looted" only ever means "there is nothing left to walk to". Anything still on
                // the record clears it, so falling through to the next grave - or arriving in
                // another world with an unlooted grave in it - lights the compass again.
                if (_graves.Count > 0) _looted = false;
            }
            catch (Exception e)
            {
                _graves = new List<Grave>(); _tracked = null; _haveGrave = false; _cacheValid = false;
                Log.LogWarning("[CorpseRun] grave record read: " + e.Message);
            }
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

        /// <summary>
        /// A tombstone was looted or emptied. Since 0.9.1 this removes THE RECORD THAT STONE
        /// BELONGS TO - matched by ZDOID when we have one, otherwise the nearest of our own records
        /// within LootMatchDistance - rather than blindly the tracked one, which with several
        /// graves on the go would have thrown away the wrong marker. If any graves are left,
        /// tracking falls through to the next best instead of switching everything off.
        /// </summary>
        private static void OnGraveTouched(TombStone ts, string how)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            try
            {
                var me = Player.m_localPlayer;
                if (ts == null || me == null) return;
                RefreshGraves();
                if (_graves.Count == 0) return;

                long zu = 0L; uint zi = 0u;
                var nview = ts.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    var zdo = nview.GetZDO();
                    if (zdo != null) { zu = zdo.m_uid.UserID; zi = zdo.m_uid.ID; }
                }

                Vector3 stone = ts.transform.position;
                var hit = GraveRecord.Match(_graves, stone, zu, zi, _inst._lootMatchDistance.Value);
                if (hit == null) return;                          // somebody else's grave

                bool wasTracked = _tracked != null &&
                                  string.Equals(hit.Id, _tracked.Id, StringComparison.Ordinal);
                int rank = IndexOf(hit);
                float d = Vector3.Distance(stone, hit.Pos);

                if (ts.m_lootStatusEffect != null) _corpseRunHash = ts.m_lootStatusEffect.NameHash();
                _lootDistanceFromHome = Vector3.Distance(hit.Pos, HomePoint());

                GraveRecord.Remove(me, hit.Id);
                if (wasTracked) _trackedId = null;                // let the next best take over
                RefreshGraves(true);

                _looted = _graves.Count == 0;
                if (_looted) { GraveCompassHud.Hide(); RemovePull(); }

                if (_inst._scaledEnabled.Value)
                {
                    // The SE may land a frame or two later (SEMan.AddStatusEffect on the owner),
                    // so scaling is retried from the tick for a few seconds instead of right here.
                    _scalePending = true;
                    _scalePendingUntil = Time.time + 5f;
                }
                Log.LogInfo("[CorpseRun] grave #" + rank + " (" + hit.ItemsText + ") " + how + " " +
                            d.ToString("0.0") + "m from its recorded point - " +
                            (_graves.Count == 0
                                ? "no graves left, compass off, pull off"
                                : _graves.Count + " grave(s) left, now tracking " + TrackedText()) +
                            (_inst._scaledEnabled.Value ? ", CorpseRun scaling armed" : ""));
            }
            catch (Exception e) { Log.LogWarning("[CorpseRun] grave " + how + ": " + e.Message); }
        }

        /// <summary>
        /// The three grave commands. <c>nvlb.grave.clear</c> forgets the TRACKED grave by hand -
        /// the escape hatch for a grave that can no longer be reached or looted (destroyed,
        /// unreachable terrain, another mod ate it), which would otherwise keep the compass and
        /// GravePull on until the next death; with several graves on the go it now drops tracking
        /// onto the next best rather than switching everything off. <c>nvlb.grave.clear all</c>
        /// forgets every grave recorded in this world (other worlds are left alone).
        /// <c>nvlb.grave.next</c> is the console twin of CycleGraveKey, and <c>nvlb.grave.list</c>
        /// prints what is recorded.
        /// </summary>
        private static void RegisterCommand()
        {
            if (_commandRegistered) return;
            _commandRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.grave.clear",
                    "Forget the grave the compass is pointing at, falling through to the next best. " +
                    "'nvlb.grave.clear all' forgets every grave recorded in this world.",
                    new Terminal.ConsoleEvent(ClearCommand));
                new Terminal.ConsoleCommand("nvlb.grave.next",
                    "Point the Grave Compass at the next recorded grave (wraps around).",
                    new Terminal.ConsoleEvent(NextCommand));
                new Terminal.ConsoleCommand("nvlb.grave.list",
                    "List every grave recorded in this world, richest first, and which one is tracked.",
                    new Terminal.ConsoleEvent(ListCommand));
                Log.LogInfo("[CorpseRun] console commands 'nvlb.grave.clear', 'nvlb.grave.next' and " +
                            "'nvlb.grave.list' registered");
            }
            catch (Exception e)
            {
                _commandRegistered = false;
                Log.LogError("[CorpseRun] could not register the nvlb.grave commands: " + e);
            }
        }

        private static void Say(Terminal.ConsoleEventArgs args, string msg)
        {
            if (args != null && args.Context != null) args.Context.AddString("[CorpseRun] " + msg);
            Log.LogInfo("[CorpseRun] " + msg);
        }

        private static void ClearCommand(Terminal.ConsoleEventArgs args)
        {
            var me = Player.m_localPlayer;
            bool all = args != null && args.Args != null && args.Args.Length > 1 &&
                       string.Equals(args.Args[1].Trim(), "all", StringComparison.OrdinalIgnoreCase);

            RefreshGraves(true);
            if (_graves.Count == 0)
            {
                _haveGrave = false; _graveWorld = ""; _lastDistance = -1f; _lootDistanceFromHome = -1f;
                GraveCompassHud.Hide(); RemovePull();
                Say(args, "no grave record to clear in this world");
                return;
            }

            string msg;
            if (all)
            {
                int n = GraveRecord.ClearWorld(me);
                _trackedId = null;
                RefreshGraves(true);
                msg = n + " grave record(s) cleared in this world" +
                      (GraveRecord.ReadAll(me).Count > 0
                          ? " (records from other worlds left alone)" : "");
            }
            else
            {
                var gone = _tracked;
                GraveRecord.Remove(me, gone.Id);
                _trackedId = null;
                RefreshGraves(true);
                msg = "grave cleared (was " + gone.Describe() + ")" +
                      (_graves.Count > 0
                          ? " - now tracking " + TrackedText() + " of " + _graves.Count
                          : " - no graves left");
            }

            _lastDistance = -1f;
            _lootDistanceFromHome = -1f;
            if (_graves.Count == 0) { GraveCompassHud.Hide(); RemovePull(); }
            Say(args, msg);
        }

        private static void NextCommand(Terminal.ConsoleEventArgs args)
        {
            if (!CycleTracked())
            {
                Say(args, _graves.Count == 0 ? "no graves recorded in this world"
                                             : "only one grave recorded - nothing to cycle to");
                return;
            }
            Say(args, "now tracking " + TrackedText() + " of " + _graves.Count + ": " + _tracked.Describe());
        }

        private static void ListCommand(Terminal.ConsoleEventArgs args)
        {
            var me = Player.m_localPlayer;
            RefreshGraves(true);
            if (_graves.Count == 0)
            {
                Say(args, "no graves recorded in this world (" + GraveRecord.ReadAll(me).Count +
                          " recorded in all worlds)");
                return;
            }
            Say(args, _graves.Count + " grave(s) in '" + GraveRecord.CurrentWorld() + "', richest first:");
            for (int i = 0; i < _graves.Count; i++)
            {
                var g = _graves[i];
                float d = me != null ? Vector3.Distance(me.transform.position, g.Pos) : -1f;
                Say(args, "  #" + (i + 1) + (g == _tracked ? " *" : "  ") + " " + g.ItemsText +
                          "  (" + Mathf.RoundToInt(g.Pos.x) + ", " + Mathf.RoundToInt(g.Pos.z) + ")" +
                          (d >= 0f ? "  " + Mathf.RoundToInt(d) + " m away" : ""));
            }
        }

        /// <summary>Move tracking on one grave, wrapping. False when there is nothing to move to.</summary>
        private static bool CycleTracked()
        {
            RefreshGraves(true);
            if (_graves.Count < 2) return false;
            var next = GraveRecord.Next(_graves, _trackedId);
            if (next == null) return false;
            _trackedId = next.Id;
            RefreshGraves();
            _looted = false;
            return true;
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

            RefreshGraves();

            // --- next grave (0.9.1) ---
            // Only worth a keystroke when there is more than one grave; the gating is the same as
            // the dismiss hold's, because ZInput reads the raw device and knows nothing about chat,
            // the console or an open menu.
            if (_graves.Count > 1 && CycleKeyLabel().Length > 0 && DualPowersModule.InputAllowed(me) &&
                NvlbKeys.Down("GraveCycle") && CycleTracked())
            {
                me.Message(MessageHud.MessageType.Center,
                           "Grave " + IndexOf(_tracked) + "/" + _graves.Count + " · " + _tracked.ItemsText);
                Log.LogInfo("[CorpseRun] cycled to grave " + TrackedText() + " of " + _graves.Count);
            }

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
            GraveCompassHud.SetItems(_tracked != null && _tracked.ItemsKnown ? _tracked.Items : -1);

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
            if (!compassShown || !_haveGrave || _looted)
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint("");
                return;
            }

            string more = MoreGravesHint();
            string dismissKey = DismissKeyLabel();
            if (dismissKey.Length == 0)
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint(more);            // still say there are others to cycle to
                return;
            }
            string idle = Join(more, "Hold " + dismissKey + " to dismiss grave");

            if (!DualPowersModule.InputAllowed(me))
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint(idle);
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
                    me.Message(MessageHud.MessageType.Center,
                               _graves.Count > 0
                                   ? "Grave marker cleared - " + _graves.Count + " grave(s) left"
                                   : "Grave marker cleared");
                    return;
                }
                GraveCompassHud.SetHint("Dismissing grave... " +
                                        Mathf.RoundToInt(Mathf.Clamp01(_clearHeld / need) * 100f) + "%");
            }
            else
            {
                _clearHeld = 0f;
                GraveCompassHud.SetHint(idle);
            }
        }

        /// <summary>
        /// "(+1 more)" - the 0.9.1 hint that the compass is picking one of several graves, with the
        /// cycle key named when one is bound. "" when this is the only grave, so the hint line
        /// reads exactly as it did before for the normal single-grave case.
        /// </summary>
        private static string MoreGravesHint()
        {
            int extra = _graves.Count - 1;
            if (extra <= 0) return "";
            string key = CycleKeyLabel();
            return "(+" + extra + " more" + (key.Length > 0 ? ", " + key + " to cycle" : "") + ")";
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + " · " + b;
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
            return "grave=" + grave +
                   " tracking=" + (_tracked == null ? "none" : TrackedText() + "/" + _graves.Count) +
                   " compass=" + compass +
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
        /// The 0.4.2 world-scoping cases plus the 0.9.1 multi-grave rules, driven straight through
        /// GraveRecord's dictionary-level API - no Player, no world, no ObjectDB, so the whole
        /// thing runs on a headless dedicated server with zero players.
        ///
        /// Case 1 is the original bug: a character whose .fch has had a death point since forever,
        /// joining a world it has never died in, must get NOTHING. Cases 7 onwards are Matt's
        /// 2026-09-09 report: die rich, die again poor, and the compass must stay on the rich one.
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
            Grave added; List<Grave> evicted;

            // (1) stale PlayerProfile death point, no record of ours -> everything inactive.
            check(GraveRecord.ReadWorld(data, here).Count == 0,
                  "(1) a character with a stale profile death point but no nvlb.grave record reads as NO grave " +
                  "- compass, GravePull and CorpseRunScaled all stay off");

            // (2) a death in this world -> active, and everything survives the round trip.
            GraveRecord.Add(data, here, pos, 4242.5d, 31, 7L, 99u, 1, out added, out evicted);
            var mine = GraveRecord.ReadWorld(data, here);
            check(mine.Count == 1 && (mine[0].Pos - pos).sqrMagnitude < 0.0001f &&
                  mine[0].World == here && Math.Abs(mine[0].Time - 4242.5d) < 0.001d &&
                  mine[0].Items == 31 && mine[0].ZdoUser == 7L && mine[0].ZdoId == 99u,
                  "(2) a record written in '" + here + "' reads back there: " + mine[0].Describe() +
                  " t=" + mine[0].Time + " zdo=" + mine[0].ZdoUser + ":" + mine[0].ZdoId);

            // (3) the same record, read from a different world -> inactive, and NOT destroyed.
            check(GraveRecord.ReadWorld(data, other).Count == 0,
                  "(3) the same record is invisible in '" + other + "' (the 0.4.2 bug: an old grave from " +
                  "another world used to light the compass)");
            check(GraveRecord.ReadWorld(data, here).Count == 1,
                  "(3b) ...and it is left in place, so returning to '" + here + "' still finds the grave");

            // (4) THE 0.9.1 BUG. Die again on the run back with two mushrooms on you: the poor
            //     grave is recorded, but the compass must NOT move to it.
            GraveRecord.Add(data, here, new Vector3(1000f, 0f, -600f), 4300d, 2, 0L, 0u, 1,
                            out added, out evicted);
            mine = GraveRecord.ReadWorld(data, here);
            check(mine.Count == 2 && GraveRecord.Best(mine).Items == 31,
                  "(4) a second, near-empty death is remembered but the richest grave still wins: " +
                  "tracking " + GraveRecord.Best(mine).Describe() + " of " + mine.Count);

            // (5) MinItemsToTrack: a death below the floor is not recorded at all.
            check(!GraveRecord.Add(data, here, new Vector3(1f, 0f, 1f), 4400d, 2, 0L, 0u, 5,
                                   out added, out evicted) &&
                  GraveRecord.ReadWorld(data, here).Count == 2,
                  "(5) MinItemsToTrack=5 keeps a 2-item grave out of the record entirely");
            check(!GraveRecord.Add(data, here, new Vector3(2f, 0f, 2f), 4500d, 0, 0L, 0u, 1,
                                   out added, out evicted) &&
                  GraveRecord.ReadWorld(data, here).Count == 2,
                  "(6) a death with 0 items records nothing - vanilla spawns no tombstone for an " +
                  "empty inventory (Player.CreateTombStone)");

            // (7) looting the RIGHT grave: the poor one is matched by position and removed, and the
            //     rich one is untouched. This is what OnGraveTouched does.
            mine = GraveRecord.ReadWorld(data, here);
            var hit = GraveRecord.Match(mine, new Vector3(1001f, 1.5f, -601f), 0L, 0u, 20f);
            check(hit != null && hit.Items == 2,
                  "(7) a tombstone at the poor grave matches the POOR record, not the tracked one");
            GraveRecord.Remove(data, hit.Id);
            mine = GraveRecord.ReadWorld(data, here);
            check(mine.Count == 1 && mine[0].Items == 31,
                  "(7b) ...and removing it leaves the 31-item grave still tracked");

            // (8) an exact ZDOID match beats distance, so a drifted tombstone still matches.
            var far = GraveRecord.Match(GraveRecord.ReadWorld(data, here),
                                        new Vector3(9999f, 0f, 9999f), 7L, 99u, 20f);
            check(far != null && far.Items == 31,
                  "(8) a tombstone 12 km from its recorded point still matches on ZDOID");

            // (9) tracking falls through: loot the rich one and nothing is left.
            GraveRecord.Remove(data, far.Id);
            check(GraveRecord.ReadWorld(data, here).Count == 0,
                  "(9) looting the last grave leaves none - compass off, pull off");

            // (10) the per-world cap, oldest out.
            var cap = new Dictionary<string, string>();
            for (int i = 0; i < GraveRecord.MaxPerWorld + 2; i++)
                GraveRecord.Add(cap, here, new Vector3(i, 0f, 0f), 100d + i, 10 + i, 0L, 0u, 1,
                                out added, out evicted);
            var capped = GraveRecord.ReadWorld(cap, here);
            bool oldestGone = true;
            foreach (var g in capped) if (Math.Abs(g.Time - 100d) < 0.001d) oldestGone = false;
            check(capped.Count == GraveRecord.MaxPerWorld && oldestGone,
                  "(10) only " + GraveRecord.MaxPerWorld + " graves are kept per world and the " +
                  "oldest is the one that goes (" + capped.Count + " kept)");

            // (11) cycling wraps all the way round.
            string id = GraveRecord.Best(capped).Id;
            string firstId = id;
            for (int i = 0; i < capped.Count; i++) id = GraveRecord.Next(capped, id).Id;
            check(id == firstId,
                  "(11) cycling " + capped.Count + " times with nvlb.grave.next / CycleGraveKey " +
                  "comes back to the grave it started on");
            check(GraveRecord.Next(capped, "no-such-grave") == GraveRecord.Best(capped),
                  "(11b) cycling from a grave that was just looted starts again at the richest");

            // (12) migration: a v1 single-grave record from 0.9.0 and earlier must still arrive.
            var oldFmt = new Dictionary<string, string> {
                { GraveRecord.Key, "1|" + here + "|1234.5|-12.25|-678.75|4242.5" } };
            var migrated = GraveRecord.ReadWorld(oldFmt, here);
            check(migrated.Count == 1 && (migrated[0].Pos - pos).sqrMagnitude < 0.0001f &&
                  !migrated[0].ItemsKnown,
                  "(12) a pre-0.9.1 single-grave record loads as a one-entry list with an unknown " +
                  "item count - nobody loses a grave marker on update");
            GraveRecord.Add(oldFmt, here, new Vector3(5f, 0f, 5f), 4999d, 12, 0L, 0u, 1,
                            out added, out evicted);
            var mixed = GraveRecord.ReadWorld(oldFmt, here);
            check(mixed.Count == 2 && GraveRecord.Best(mixed).Items == 12,
                  "(12b) ...and the next death rewrites the whole record in v2 alongside it");
            var reEncoded = new Dictionary<string, string> { { GraveRecord.Key, GraveRecord.Encode(mixed) } };
            var reRead = GraveRecord.ReadWorld(reEncoded, here);
            check(reRead.Count == 2 && reRead[0].Items == 12 && !reRead[1].ItemsKnown,
                  "(12c) ...and the v2 string it produced decodes back to the same two graves, " +
                  "unknown count and all");

            // (13) a corrupt or truncated record must degrade to "no graves", never throw.
            var junk = new Dictionary<string, string> { { GraveRecord.Key, "1|OnlyTwo" } };
            check(GraveRecord.ReadWorld(junk, here).Count == 0,
                  "(13) a corrupt record decodes to no graves instead of throwing");
            var junk2 = new Dictionary<string, string> { { GraveRecord.Key, "2;bad;also|bad" } };
            check(GraveRecord.ReadWorld(junk2, here).Count == 0,
                  "(13b) a corrupt v2 entry is skipped rather than losing the whole record");

            // (14) a world name containing either separator must survive.
            var odd = new Dictionary<string, string>();
            GraveRecord.Add(odd, "a|b;c%d", pos, 1d, 3, 0L, 0u, 1, out added, out evicted);
            var oddBack = GraveRecord.ReadWorld(odd, "a|b;c%d");
            check(oddBack.Count == 1 && oddBack[0].World == "a|b;c%d",
                  "(14) a world name containing '|', ';' and '%' round trips");

            // (15) graves in other worlds are kept but never counted here.
            var multi = new Dictionary<string, string>();
            GraveRecord.Add(multi, here, pos, 1d, 5, 0L, 0u, 1, out added, out evicted);
            GraveRecord.Add(multi, other, pos, 2d, 50, 0L, 0u, 1, out added, out evicted);
            check(GraveRecord.ReadWorld(multi, here).Count == 1 &&
                  GraveRecord.ReadAll(multi).Count == 2 &&
                  GraveRecord.ClearWorld(multi, here) == 1 &&
                  GraveRecord.ReadAll(multi).Count == 1,
                  "(15) 'nvlb.grave.clear all' empties this world only - other worlds keep theirs");

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
