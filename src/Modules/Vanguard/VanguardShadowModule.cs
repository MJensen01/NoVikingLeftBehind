using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Module 5 of the catch-up set: **VanguardShadow**.
    ///
    /// A player standing near a better-geared friend gets "Vanguard's Shadow": less incoming
    /// damage, more skill XP, faster stamina regen. It never touches the frontier - it only helps
    /// the person who is behind, and only while they are actually next to the person who is ahead.
    ///
    /// How it works
    /// ------------
    /// * Every [Vanguard] UpdateSec the local player computes its own gear tier (highest
    ///   VanguardGear tier over its equipped items) and writes it to its OWN player ZDO as the
    ///   int "nvlb_geartier". ZDO members replicate to every peer, so each client can read every
    ///   nearby player's gear tier with no custom RPC at all.
    /// * The same tick scans Player.GetAllPlayers() for another player within Radius whose
    ///   replicated gear tier is >= own + TierGap. If one exists, the status effect is applied
    ///   (or its ttl reset); otherwise it is removed.
    /// * The buff itself is VanguardStatusEffect, registered into ObjectDB.m_StatusEffects from a
    ///   postfix on ObjectDB.Awake *and* ObjectDB.CopyOtherDB (CopyOtherDB replaces the whole list
    ///   with the other DB's - ObjectDB.decompiled.cs:30 - so registering only in Awake would be
    ///   silently undone when the client copies the server DB).
    ///
    /// Patches: ObjectDB.Awake (postfix), ObjectDB.CopyOtherDB (postfix), Player.Update (postfix).
    /// Nothing on the damage or skill path - see VanguardStatusEffect for why.
    ///
    /// Side is Client. [Vanguard] SelfTest (local, default false) flips it to Both so the headless
    /// server can prove the registration + gear-tier maths with no players connected; the runtime
    /// tick is gated on ClientActive() and so stays inert there either way.
    /// </summary>
    internal sealed class VanguardShadowModule : FeatureModule
    {
        public override string Name => "VanguardShadow";
        public override string Section => "Vanguard";
        public override ModuleSide Side =>
            (_selfTest != null && _selfTest.Value) ? ModuleSide.Both : ModuleSide.Client;

        public const string ZdoKey = "nvlb_geartier";

        private static readonly int ZdoHash = ZdoKey.GetStableHashCode();
        private static readonly int SeHash = VanguardStatusEffect.SeName.GetStableHashCode();

        private static VanguardShadowModule _inst;
        private static VanguardStatusEffect _template;
        private static bool _selfTestDone;

        private ConfigEntry<float> _radius;
        private ConfigEntry<int> _tierGap;
        private ConfigEntry<float> _updateSec;
        private ConfigEntry<float> _damageReduction;
        private ConfigEntry<float> _xpBonus;
        private ConfigEntry<float> _staminaRegen;
        private ConfigEntry<bool> _requireBehindFrontier;
        private ConfigEntry<bool> _includeUtility;
        private ConfigEntry<bool> _nameHeuristic;
        private ConfigEntry<string> _iconFrom;
        private ConfigEntry<bool> _selfTest;

        /// <summary>Read by VanguardGear (step 3 of the tier resolution).</summary>
        public static bool NameHeuristic =>
            _inst == null || _inst._nameHeuristic == null || _inst._nameHeuristic.Value;

        // ---- live state, for nvlb.status ------------------------------------------------
        private static int _myTier = -1;
        private static int _bestTier = -1;
        private static float _bestDist = -1f;
        private static string _bestName;
        private static bool _buffOn;
        private static float _accum;
        private static int _tickErrors;

        // ---- config ----------------------------------------------------------------------

        protected override void Bind()
        {
            _radius = BindSynced("Radius", 20f,
                "Metres. A player this close to a better-geared player gets Vanguard's Shadow.");
            _tierGap = BindSynced("TierGap", 1,
                "How many gear tiers ahead the other player must be. 1 = one tier ahead is enough.");
            _updateSec = BindSynced("UpdateSec", 5f,
                "Seconds between gear-tier writes and proximity scans. Also drives the buff's " +
                "safety-net lifetime (3x this).");
            _damageReduction = BindSynced("DamageReduction", 0.25f,
                "Fraction of incoming damage removed while the buff is up (0.25 = take 75%). " +
                "Applied before armour and resistances, like the game's own hit modifiers.");
            _xpBonus = BindSynced("XpBonus", 0.5f,
                "Extra skill XP while the buff is up (0.5 = +50%).");
            _staminaRegen = BindSynced("StaminaRegen", 0.2f,
                "Extra stamina regeneration while the buff is up (0.2 = +20%). 0 disables it.");
            _requireBehindFrontier = BindSynced("RequireBehindFrontier", false,
                "Also require the player's own gear tier to be behind the world's frontier " +
                "([Frontier] WorldTier minus TiersBehind). False = proximity and the tier gap " +
                "are the only conditions.");
            _includeUtility = BindSynced("IncludeUtility", false,
                "Count the utility slot (Megingjord, Wishbone...) towards gear tier.");
            _nameHeuristic = BindSynced("NameHeuristic", true,
                "If an item has no crafting recipe, fall back to matching material names inside " +
                "the item's prefab name (e.g. anything containing 'Iron' is tier 2).");
            _iconFrom = BindSynced("IconFrom", "Rested",
                "Name of an existing status effect whose icon Vanguard's Shadow reuses. The mod " +
                "ships no assets of its own.");
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, machine-local. Runs the module on a dedicated server too and logs a " +
                "registration + gear-tier self test at world load. Leave false in normal use.");

            _inst = this;
            PushNumbers();
        }

        private void PushNumbers()
        {
            VanguardStatusEffect.DamageReduction = _damageReduction.Value;
            VanguardStatusEffect.XpBonus = _xpBonus.Value;
            VanguardStatusEffect.StaminaRegen = _staminaRegen.Value;
            if (_template != null)
            {
                _template.m_ttl = Mathf.Max(1f, _updateSec.Value * 3f);
                _template.m_tooltip = Tooltip();
            }
        }

        private string Tooltip()
        {
            return "Fighting in the shadow of a better-geared ally.\n" +
                   "Incoming damage -" + Mathf.RoundToInt(_damageReduction.Value * 100f) + "%\n" +
                   "Skill XP +" + Mathf.RoundToInt(_xpBonus.Value * 100f) + "%\n" +
                   (_staminaRegen.Value != 0f
                       ? "Stamina regen +" + Mathf.RoundToInt(_staminaRegen.Value * 100f) + "%\n"
                       : "") +
                   "Within " + Mathf.RoundToInt(_radius.Value) + " m of a player at least " +
                   _tierGap.Value + " gear tier(s) ahead.";
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            PushNumbers();
            if (entry == _nameHeuristic) VanguardGear.Invalidate();
            if (entry == EnabledCfg && !EnabledCfg.Value) RemoveFromLocalPlayer();
            if (entry == _selfTest)
                Log.LogInfo("[VanguardShadow] SelfTest=" + _selfTest.Value +
                            " takes effect on the next game start (module side is decided at load).");
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

            // Sanity-check the vanilla hooks we rely on instead of patching, so a game update that
            // renames or removes them fails loudly here rather than silently doing nothing.
            foreach (var sig in new[] { "OnDamaged", "ModifyRaiseSkill", "ModifyStaminaRegen" })
                if (AccessTools.Method(typeof(SEMan), sig) == null)
                    throw new Exception("SEMan." + sig + " not found - the buff would be inert");

            var post = new HarmonyMethod(typeof(VanguardShadowModule), nameof(ObjectDBPostfix));
            Harmony.Patch(awake, postfix: post);
            Harmony.Patch(copy, postfix: post);
            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(VanguardShadowModule), nameof(PlayerUpdatePostfix)));

            EnsureTemplate();
            Log.LogInfo("[VanguardShadow] radius=" + _radius.Value + "m tierGap=" + _tierGap.Value +
                        " every " + _updateSec.Value + "s -> damage -" +
                        Mathf.RoundToInt(_damageReduction.Value * 100f) + "% xp +" +
                        Mathf.RoundToInt(_xpBonus.Value * 100f) + "% stamina +" +
                        Mathf.RoundToInt(_staminaRegen.Value * 100f) + "%" +
                        " requireBehindFrontier=" + _requireBehindFrontier.Value +
                        " includeUtility=" + _includeUtility.Value +
                        " zdoKey=" + ZdoKey + " selfTest=" + _selfTest.Value);
        }

        public override void Disable()
        {
            RemoveFromLocalPlayer();
            try
            {
                var odb = ObjectDB.instance;
                if (odb != null && odb.m_StatusEffects != null && _template != null)
                    odb.m_StatusEffects.Remove(_template);
            }
            catch (Exception e) { Log.LogWarning("[VanguardShadow] deregister failed: " + e.Message); }
            base.Disable();
        }

        // ---- status effect registration ---------------------------------------------------

        private static void EnsureTemplate()
        {
            if (_template != null) return;
            _template = VanguardStatusEffect.Create();
            if (_inst != null) _inst.PushNumbers();
        }

        /// <summary>Postfix for both ObjectDB.Awake and ObjectDB.CopyOtherDB.</summary>
        private static void ObjectDBPostfix(ObjectDB __instance)
        {
            if (_inst == null || !_inst.Active) return;
            try
            {
                Register(__instance);
                VanguardGear.Invalidate();
                if (_inst._selfTest.Value) RunSelfTest(__instance);
            }
            catch (Exception e)
            {
                Log.LogError("[VanguardShadow] ObjectDB registration failed: " + e);
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
                if (se != null && se.name == VanguardStatusEffect.SeName)
                {
                    if (!ReferenceEquals(se, _template)) { odb.m_StatusEffects[i] = _template; break; }
                    return;
                }
            }

            BorrowIcon(odb);
            odb.m_StatusEffects.Add(_template);
            Log.LogInfo("[VanguardShadow] registered status effect '" + VanguardStatusEffect.SeName +
                        "' (hash " + SeHash + ") in ObjectDB, " + odb.m_StatusEffects.Count + " total" +
                        (_template.m_icon != null ? ", icon borrowed from '" + _inst._iconFrom.Value + "'"
                                                  : ", NO ICON (donor not found)"));
        }

        /// <summary>Reuse an existing status effect's sprite - the mod ships no assets.</summary>
        private static void BorrowIcon(ObjectDB odb)
        {
            if (_template.m_icon != null) return;
            string want = _inst != null ? _inst._iconFrom.Value : "Rested";
            Sprite any = null;
            foreach (var se in odb.m_StatusEffects)
            {
                if (se == null || se.m_icon == null) continue;
                if (any == null) any = se.m_icon;
                if (string.Equals(se.name, want, StringComparison.OrdinalIgnoreCase))
                {
                    _template.m_icon = se.m_icon;
                    return;
                }
            }
            _template.m_icon = any;   // null on a headless server: no sprites there, and no HUD either.
        }

        // ---- the tick ----------------------------------------------------------------------

        private static void PlayerUpdatePostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;

            _accum += Time.deltaTime;
            if (_accum < Mathf.Max(0.5f, _inst._updateSec.Value)) return;
            _accum = 0f;

            try { _inst.Tick(__instance); }
            catch (Exception e)
            {
                if (_tickErrors++ < 3)
                    Log.LogError("[VanguardShadow] tick failed (" + _tickErrors + "/3): " + e);
            }
        }

        private void Tick(Player me)
        {
            // 1. publish our own gear tier on our player ZDO (replicates to every peer).
            _myTier = VanguardGear.OfHumanoid(me, _includeUtility.Value);
            var nview = me.m_nview;
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                if (zdo != null && zdo.GetInt(ZdoHash, -1) != _myTier)
                    zdo.Set(ZdoHash, _myTier);
            }

            // 2. is anyone nearby far enough ahead?
            _bestTier = -1; _bestDist = -1f; _bestName = null;
            int need = _myTier + Mathf.Max(1, _tierGap.Value);
            float radius = _radius.Value;
            var pos = me.transform.position;

            List<Player> all = Player.GetAllPlayers();
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p == me || p.IsDead()) continue;
                float d = Vector3.Distance(pos, p.transform.position);
                if (d > radius) continue;
                int t = VanguardGear.FromZdo(p, ZdoHash);
                if (t < need) continue;
                if (_bestTier < 0 || t > _bestTier || (t == _bestTier && d < _bestDist))
                {
                    _bestTier = t; _bestDist = d; _bestName = p.GetPlayerName();
                }
            }

            bool eligible = _bestTier >= 0;

            // 3. optional frontier gate: only help players whose kit is behind the world's progress.
            if (eligible && _requireBehindFrontier.Value)
            {
                int behindCutoff = Frontier.WorldTier -
                                   (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1);
                if (_myTier > behindCutoff) eligible = false;
            }

            // 4. apply / refresh / remove.
            var seman = me.GetSEMan();
            if (seman == null) return;
            var live = seman.GetStatusEffect(SeHash);

            if (eligible)
            {
                if (live == null)
                {
                    EnsureTemplate();
                    if (ObjectDB.instance != null) Register(ObjectDB.instance);
                    seman.AddStatusEffect(_template, false);
                    _buffOn = true;
                    Log.LogInfo("[VanguardShadow] buff ON (own tier " + _myTier + ", vanguard " +
                                (_bestName ?? "?") + " tier " + _bestTier + " at " +
                                _bestDist.ToString("0.0") + "m)");
                }
                else
                {
                    live.ResetTime();
                    _buffOn = true;
                }
            }
            else if (live != null)
            {
                seman.RemoveStatusEffect(SeHash, true);
                _buffOn = false;
                Log.LogInfo("[VanguardShadow] buff OFF (own tier " + _myTier + ", no vanguard within " +
                            radius + "m at tier " + need + "+)");
            }
            else
            {
                _buffOn = false;
            }
        }

        private static void RemoveFromLocalPlayer()
        {
            try
            {
                var me = Player.m_localPlayer;
                if (me == null) return;
                var seman = me.GetSEMan();
                if (seman != null && seman.HaveStatusEffect(SeHash)) seman.RemoveStatusEffect(SeHash, true);
                _buffOn = false;
            }
            catch (Exception e) { Log.LogWarning("[VanguardShadow] buff removal failed: " + e.Message); }
        }

        // ---- nvlb.status ------------------------------------------------------------------

        public override string StatusDetail()
        {
            var s = "gearTier=" + (_myTier < 0 ? "?" : _myTier.ToString()) +
                    " radius=" + _radius.Value + "m gap=" + _tierGap.Value +
                    " dmg-" + Mathf.RoundToInt(_damageReduction.Value * 100f) +
                    "% xp+" + Mathf.RoundToInt(_xpBonus.Value * 100f) +
                    "% sta+" + Mathf.RoundToInt(_staminaRegen.Value * 100f) + "%";
            s += _bestTier >= 0
                ? "  nearestVanguard=" + (_bestName ?? "?") + " tier " + _bestTier +
                  " @ " + _bestDist.ToString("0.0") + "m"
                : "  nearestVanguard=none";
            return s + "  buff=" + (_buffOn ? "ACTIVE" : "off");
        }

        // ---- self test (headless, 0 players) -------------------------------------------------

        private static void RunSelfTest(ObjectDB odb)
        {
            if (_selfTestDone) return;
            _selfTestDone = true;

            Log.LogInfo("[VanguardShadow] SelfTest: --- begin ---");

            var found = odb.GetStatusEffect(SeHash);
            Log.LogInfo("[VanguardShadow] SelfTest: ObjectDB.GetStatusEffect(\"" +
                        VanguardStatusEffect.SeName + "\".GetStableHashCode()=" + SeHash + ") -> " +
                        (found == null ? "NULL  *** FAIL ***"
                                       : "'" + found.name + "' / m_name='" + found.m_name +
                                         "' ttl=" + found.m_ttl + " icon=" +
                                         (found.m_icon != null ? "yes" : "none (headless)") +
                                         (ReferenceEquals(found, _template) ? " SAME INSTANCE  OK" : " *** WRONG INSTANCE ***")));
            Log.LogInfo("[VanguardShadow] SelfTest: is VanguardStatusEffect=" +
                        (found is VanguardStatusEffect) + ", list size=" + odb.m_StatusEffects.Count +
                        ", tooltip=" + (found != null ? found.m_tooltip.Replace("\n", " | ") : "-"));

            // Gear tier over a fake equipped kit, by prefab name.
            var kit = new[] { "ArmorBronzeChest", "AxeIron", "ArmorLeatherLegs", "Bronze", "Wood" };
            int max = 0;
            foreach (var n in kit)
            {
                int t = VanguardGear.OfName(n);
                if (t > max) max = t;
                Log.LogInfo("[VanguardShadow] SelfTest: gearTier(" + n + ") = " + t);
            }
            Log.LogInfo("[VanguardShadow] SelfTest: kit gear tier = " + max +
                        " (expect 2: ArmorBronzeChest 1, AxeIron 2)");
            Log.LogInfo("[VanguardShadow] SelfTest: zdo key '" + ZdoKey + "' hash=" + ZdoHash +
                        ", WorldTier=" + Frontier.WorldTier);
            Log.LogInfo("[VanguardShadow] SelfTest: --- end ---");
        }
    }
}
