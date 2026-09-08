using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// OverkillTools - a tool that outclasses the material works faster.
    ///
    /// A black metal axe fells a birch in a couple of swings; an iron pickaxe bites a boulder
    /// harder than an antler one. The Meadows on day one are untouched, because the bonus is
    /// driven entirely by the GAP between the hit's tool tier and the target's own
    /// <c>m_minToolTier</c>: a stone axe (tier 0) on a beech (tier 0) has gap 0 and stays exactly
    /// vanilla.
    ///
    ///     gap        = hit.m_toolTier - target.m_minToolTier
    ///     multiplier = gap &lt; 1 ? 1 : min(1 + PerTierBonus * gap, MaxMultiplier)
    ///
    /// and the whole hit is scaled with <c>HitData.DamageTypes.Modify(float)</c>, so a chop hit
    /// scales its chop and a pickaxe hit its pickaxe, with no per-damage-type special cases.
    ///
    /// DETERMINISTIC BY CONSTRUCTION. The formula reads only two numbers: the target prefab's
    /// <c>m_minToolTier</c> (identical in every install's assets) and <c>hit.m_toolTier</c>
    /// (serialised into the hit and sent over the wire - HitData.Serialize writes m_toolTier).
    /// Nothing about who owns the object, which machine runs the RPC, or the local player's
    /// state enters into it. That is why this module is Side = Both and why its patch bodies do
    /// NOT gate on ClientActive(): whoever owns the object - an owning client, a listen-server
    /// host, or the dedicated server for an object no player has claimed - computes the same
    /// number.
    ///
    /// HOOKS (0.221.13 decompile). Each is the owner-side damage handler that already early-
    /// returns on the tool tier via <c>hit.CheckToolTier(m_minToolTier, ...)</c>, so prefixing it
    /// scales exactly the hits that were going to land and nothing else:
    ///
    ///   TreeBase.RPC_Damage(long, HitData)          standing trees          [AffectTrees]
    ///   TreeLog.RPC_Damage(long, HitData)           felled logs + stumps    [AffectTrees]
    ///   MineRock.RPC_Hit(long, HitData, int)        old-style rock          [AffectRocks]
    ///   MineRock5.RPC_Damage(long, HitData, int)    boulders / rock walls   [AffectRocks]
    ///   Destructible.RPC_Damage(long, HitData)      bushes, small rocks     [AffectDestructibles]
    ///
    /// Every one of those begins with an <c>IsOwner()</c> check, so the prefix cannot fire on a
    /// non-owner. <c>Character</c>, <c>Player</c> and <c>WearNTear</c> are deliberately NOT
    /// patched: creatures, players and build pieces never see this module at all.
    ///
    /// WHO COUNTS AS A TOOL HIT. The bonus needs an attacking PLAYER swinging a real tool:
    ///   - <c>HitData.HitType.Structural</c> is rejected outright. MineRock5.CheckSupport()
    ///     manufactures a hit with <c>m_toolTier = 100</c> and that hit type to collapse an
    ///     unsupported area; without this check it would be multiplied by MaxMultiplier.
    ///   - if <c>hit.GetAttacker()</c> resolves to a Player, the bonus applies.
    ///   - if it resolves to a non-Player Character, the bonus does NOT apply (a troll flattening
    ///     a forest keeps vanilla numbers).
    ///   - if it resolves to null - the attacker's GameObject is not instantiated on THIS machine,
    ///     which happens when the object's owner is further away than the attacker's own view -
    ///     the bonus applies only when <c>hit.m_toolTier &gt; 0</c>. A tool tier above zero is
    ///     only ever set from <c>ItemDrop.ItemData.SharedData.m_toolTier</c> on a swung item, so
    ///     it is the strongest available evidence that this was a player's tool. Choosing "apply"
    ///     here rather than "skip" keeps the result identical no matter which machine happens to
    ///     own the tree, which is the whole point of the module.
    ///
    /// NO DOUBLE-DIPPING WITH FastMining. Ore nodes are FastMining's domain ([Mining]
    /// SpeedMultiplier, tier-gated on the frontier) and this module skips every prefab in that
    /// module's <c>OreNodes</c> allowlist, fractured stages included - whether or not FastMining
    /// itself is enabled. Copper, tin, silver, obsidian and meteorite therefore behave exactly as
    /// they did in 0.5.1; stone boulders, trees, logs, stumps and plain destructibles are
    /// Overkill's.
    ///
    /// GUARDRAILS: the multiplier is never below 1 (this module cannot make anything slower), the
    /// cap is hard, and a disabled sub-toggle skips its whole family before any work is done.
    /// </summary>
    internal sealed class OverkillToolsModule : FeatureModule
    {
        public override string Name => "OverkillTools";
        public override string Section => "Tools";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Theme => "Building & gathering";
        public override string Hint => "Late-game tools chew through early-game trees and rocks faster";

        protected override string EnabledDescription =>
            "A tool whose tier is above the target's required tool tier does more damage per " +
            "swing, so late-game axes and pickaxes clear early-game material fast. Deterministic " +
            "and identical on every machine; ore nodes are left to [Mining] FastMining.";

        private static ConfigEntry<float> _perTierBonus;
        private static ConfigEntry<float> _maxMultiplier;
        private static ConfigEntry<bool> _affectTrees;
        private static ConfigEntry<bool> _affectRocks;
        private static ConfigEntry<bool> _affectDestructibles;
        private static ConfigEntry<bool> _selfTest;

        private static OverkillToolsModule _self;
        private static bool _reported;

        /// <summary>Prefab hashes already logged once, so the proof line is one per prefab.</summary>
        private static readonly HashSet<int> _logged = new HashSet<int>();

        /// <summary>
        /// Sample targets for the self-test table. Deliberately a fixed list rather than a config
        /// entry: it exists to prove the maths against real prefab data, not to be tuned. Names
        /// that a future game build renames simply drop out of the table and are listed as
        /// missing.
        /// </summary>
        private static readonly string[] SelfTestTargets =
        {
            "Beech1", "Birch1", "Oak1", "FirTree", "Pinetree_01", "SwampTree1",
            "YggaShoot1", "Beech_small1", "Birch2", "PineTree_log",
            "beech_log", "Birch_log", "Oak_log", "FirTree_log", "stubbe",
            "rock4_forest", "rock4_forest_frac", "rock1_mountain", "rock1_mountain_frac",
            "rock4_heath", "MineRock_Stone", "Rock_3", "Rock_4", "rock3_mountain",
            "RockDolmen_1", "rock_mistlands1"
        };

        /// <summary>Applied AND enabled. No side gate: see the class doc - the result must not
        /// depend on which machine owns the object.</summary>
        private static bool Live()
        {
            return _self != null && _self.Active;
        }

        // ---- config -------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _perTierBonus = BindSynced("PerTierBonus", 0.5f,
                "Extra damage per tool tier ABOVE the target's own required tier. 0.5 = +50% per " +
                "tier of overkill: one tier above is x1.5, two x2.0, four x3.0 (capped by " +
                "MaxMultiplier). 0 turns the module off without unpatching it.",
                Opt.N("Extra damage per tool tier above the target's required tier", 0, 3.0, 0.05));

            _maxMultiplier = BindSynced("MaxMultiplier", 3.0f,
                "Hard cap on the damage multiplier, however large the tier gap. Clamped to a " +
                "minimum of 1.0 - this module can never make a hit weaker than vanilla.",
                Opt.N("Highest damage multiplier overkill tools can reach", 1, 10, 0.5));

            _affectTrees = BindSynced("AffectTrees", true,
                "Apply to standing trees (TreeBase) and to felled logs and stumps (TreeLog).",
                Opt.B("Apply the overkill bonus to trees and logs"));

            _affectRocks = BindSynced("AffectRocks", true,
                "Apply to mineable rock (MineRock5 boulders and rock walls, and the older " +
                "MineRock). Ore nodes listed in [Mining] OreNodes are always excluded - they " +
                "belong to FastMining.",
                Opt.B("Apply the overkill bonus to mineable rocks"));

            _affectDestructibles = BindSynced("AffectDestructibles", true,
                "Apply to plain Destructible objects: bushes, small surface rocks and similar " +
                "scenery. Ore nodes listed in [Mining] OreNodes are always excluded.",
                Opt.B("Apply the overkill bonus to bushes and small rocks"));

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Once per world load, log a table of " +
                "sample targets (tree / log / rock / destructible) with the component family and " +
                "m_minToolTier read from the real prefabs, and the resulting damage multiplier " +
                "for tool tiers 0-4. Changes no game state. Leave false in normal use.",
                Opt.B("Log a one-time table proving the tool tier maths").Admin());
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override void Disable()
        {
            base.Disable();
            if (_self == this) _self = null;
        }

        // ---- patches ------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            Patch(typeof(TreeBase), "RPC_Damage", new[] { typeof(long), typeof(HitData) },
                  nameof(TreeBasePre));
            Patch(typeof(TreeLog), "RPC_Damage", new[] { typeof(long), typeof(HitData) },
                  nameof(TreeLogPre));
            Patch(typeof(MineRock), "RPC_Hit", new[] { typeof(long), typeof(HitData), typeof(int) },
                  nameof(MineRockPre));
            Patch(typeof(MineRock5), "RPC_Damage", new[] { typeof(long), typeof(HitData), typeof(int) },
                  nameof(MineRock5Pre));
            Patch(typeof(Destructible), "RPC_Damage", new[] { typeof(long), typeof(HitData) },
                  nameof(DestructiblePre));

            // The summary + self-test line. ZoneSystem.Start is the same "world is ready" point
            // the other headless proofs use: ZNetScene's prefab list is populated by then on both
            // halves, and the boss keys have been loaded so the line can name the real tier.
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(OverkillToolsModule), nameof(WorldReady)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private void Patch(Type type, string method, Type[] args, string prefix)
        {
            var target = AccessTools.Method(type, method, args);
            if (target == null)
                throw new Exception(type.Name + "." + method + "(" + Describe(args) + ") not found");
            Harmony.Patch(target, prefix: new HarmonyMethod(typeof(OverkillToolsModule), prefix));
        }

        private static string Describe(Type[] args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(args[i].Name);
            }
            return sb.ToString();
        }

        // ---- the five owner-side prefixes -----------------------------------------------------

        private static void TreeBasePre(TreeBase __instance, HitData hit)
        {
            if (_affectTrees == null || !_affectTrees.Value) return;
            Apply(hit, __instance.m_minToolTier, __instance.m_nview, "tree");
        }

        private static void TreeLogPre(TreeLog __instance, HitData hit)
        {
            if (_affectTrees == null || !_affectTrees.Value) return;
            Apply(hit, __instance.m_minToolTier, __instance.m_nview, "log");
        }

        private static void MineRockPre(MineRock __instance, HitData hit)
        {
            if (_affectRocks == null || !_affectRocks.Value) return;
            Apply(hit, __instance.m_minToolTier, __instance.m_nview, "rock");
        }

        private static void MineRock5Pre(MineRock5 __instance, HitData hit)
        {
            if (_affectRocks == null || !_affectRocks.Value) return;
            Apply(hit, __instance.m_minToolTier, __instance.m_nview, "rock5");
        }

        private static void DestructiblePre(Destructible __instance, HitData hit)
        {
            if (_affectDestructibles == null || !_affectDestructibles.Value) return;
            Apply(hit, __instance.m_minToolTier, __instance.m_nview, "destructible");
        }

        // ---- the one place a number changes ----------------------------------------------------

        private static void Apply(HitData hit, int minToolTier, ZNetView nview, string family)
        {
            if (!Live() || hit == null) return;
            try
            {
                // Cheapest test first: the multiplier is pure integer arithmetic on two fields
                // already in hand, so a hit with no tier gap costs nothing at all. Only then is
                // the attacker resolved (a ZNetScene instance lookup) or the prefab read.
                float mult = MultiplierFor(minToolTier, hit.m_toolTier);
                if (mult <= 1.0001f) return;

                if (!IsPlayerToolHit(hit)) return;

                // Ore is FastMining's, always - the prefab lookup is only reached once the
                // multiplier would actually have done something, so the common case (a tree)
                // never pays for it.
                int prefab = PrefabHash(nview);
                if (prefab != 0 && FastMiningModule.IsOreNodePrefab(prefab)) return;

                if (prefab != 0 && _logged.Add(prefab))
                    Log.LogDebug("[OverkillTools] " + family + " prefab#" + prefab +
                                 " minToolTier=" + minToolTier + " hit toolTier=" + hit.m_toolTier +
                                 " -> x" + mult.ToString("0.##", CultureInfo.InvariantCulture));

                hit.m_damage.Modify(mult);
            }
            catch (Exception e)
            {
                Log.LogError("[OverkillTools] " + family + " prefix failed: " + e.Message);
            }
        }

        /// <summary>The pure formula. Public to the self-test so the table proves the real thing.</summary>
        internal static float MultiplierFor(int minToolTier, int hitToolTier)
        {
            int gap = hitToolTier - minToolTier;
            if (gap < 1) return 1f;

            float per = _perTierBonus != null ? _perTierBonus.Value : 0f;
            if (per <= 0f || float.IsNaN(per)) return 1f;

            float cap = _maxMultiplier != null ? _maxMultiplier.Value : 1f;
            if (float.IsNaN(cap) || cap < 1f) cap = 1f;

            float m = 1f + per * gap;
            if (m > cap) m = cap;
            return m < 1f ? 1f : m;
        }

        /// <summary>See the class doc: Structural never, Player always, unknown attacker only
        /// when the hit carries a real tool tier, any other Character never.</summary>
        private static bool IsPlayerToolHit(HitData hit)
        {
            if (hit.m_hitType == HitData.HitType.Structural) return false;
            var attacker = hit.GetAttacker();
            if (attacker != null) return attacker is Player;
            return hit.m_toolTier > 0;
        }

        private static int PrefabHash(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return 0;
            var zdo = nview.GetZDO();
            return zdo == null ? 0 : zdo.GetPrefab();
        }

        // ---- reporting ---------------------------------------------------------------------------

        private string Numbers()
        {
            return "PerTierBonus=" + _perTierBonus.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                   " MaxMultiplier=" + _maxMultiplier.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                   "x Trees=" + _affectTrees.Value +
                   " Rocks=" + _affectRocks.Value +
                   " Destructibles=" + _affectDestructibles.Value +
                   " (gap 1 -> x" + MultiplierFor(0, 1).ToString("0.##", CultureInfo.InvariantCulture) +
                   ", gap 4 -> x" + MultiplierFor(0, 4).ToString("0.##", CultureInfo.InvariantCulture) +
                   "; ore nodes excluded - see [Mining] OreNodes)";
        }

        public override string StatusDetail() { return Numbers(); }

        private static void WorldReady()
        {
            if (_reported || _self == null || !_self.Active) return;
            _reported = true;
            try
            {
                Log.LogInfo("[OverkillTools] " + _self.Numbers());
                if (_selfTest != null && _selfTest.Value)
                    foreach (var line in SelfTest().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[OverkillTools] world-ready report failed: " + e);
            }
        }

        /// <summary>
        /// The proof table: real prefabs out of ZNetScene, their component family and
        /// m_minToolTier, and the multiplier this module would give a hit at tool tiers 0-4.
        /// Pure arithmetic over prefab data - it touches no ZDO and needs no player.
        /// </summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][OverkillTools] PerTierBonus=")
              .Append((_perTierBonus != null ? _perTierBonus.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" MaxMultiplier=")
              .Append((_maxMultiplier != null ? _maxMultiplier.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append("  multiplier = min(1 + PerTierBonus * (toolTier - minToolTier), MaxMultiplier), never below 1");

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                sb.Append("\n  ZNetScene has no prefabs yet - cannot resolve any target");
                return sb.ToString();
            }

            sb.Append("\n  target                      family        minTier |    t0    t1    t2    t3    t4");
            int shown = 0;
            var missing = new List<string>();

            foreach (var name in SelfTestTargets)
            {
                var go = scene.GetPrefab(name.GetStableHashCode());
                if (go == null) { missing.Add(name); continue; }

                int minTier;
                string family;
                if (!Describe(go, out family, out minTier)) { missing.Add(name + "(no damage family)"); continue; }

                bool ore = FastMiningModule.IsOreNodePrefab(name.GetStableHashCode());
                sb.Append("\n  ").Append(name.PadRight(28)).Append(family.PadRight(14))
                  .Append(minTier.ToString().PadLeft(7)).Append(" |");
                for (int t = 0; t <= 4; t++)
                    sb.Append(("x" + MultiplierFor(minTier, t).ToString("0.##", CultureInfo.InvariantCulture)).PadLeft(6));
                if (ore) sb.Append("   [ore node - FastMining's, skipped]");
                shown++;
            }

            sb.Append("\n  ").Append(shown).Append(" target(s) resolved");
            if (missing.Count > 0)
                sb.Append("; not in ZNetScene (ignored): ").Append(string.Join(", ", missing.ToArray()));
            return sb.ToString();
        }

        /// <summary>Which of the five patched families this prefab belongs to, and its
        /// m_minToolTier. False when the prefab carries none of them.</summary>
        private static bool Describe(GameObject go, out string family, out int minToolTier)
        {
            var tree = go.GetComponentInChildren<TreeBase>(true);
            if (tree != null) { family = "TreeBase"; minToolTier = tree.m_minToolTier; return true; }

            var log = go.GetComponentInChildren<TreeLog>(true);
            if (log != null) { family = "TreeLog"; minToolTier = log.m_minToolTier; return true; }

            var mr5 = go.GetComponentInChildren<MineRock5>(true);
            if (mr5 != null) { family = "MineRock5"; minToolTier = mr5.m_minToolTier; return true; }

            var mr = go.GetComponentInChildren<MineRock>(true);
            if (mr != null) { family = "MineRock"; minToolTier = mr.m_minToolTier; return true; }

            var d = go.GetComponentInChildren<Destructible>(true);
            if (d != null) { family = "Destructible"; minToolTier = d.m_minToolTier; return true; }

            family = null;
            minToolTier = 0;
            return false;
        }
    }
}
