using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// FastMining - ore nodes whose material tier is behind the frontier take pickaxe damage
    /// multiplied by SpeedMultiplier (default 3x), optionally drop more (DropMultiplier) and
    /// optionally ignore the pickaxe's tool-tier requirement (IgnoreToolTier).
    ///
    /// COVERS ALL THREE ORE FAMILIES ON 0.221.12 (verified live, see Modules/Regrowth/
    /// OreRegrowthModule.cs): rock4_copper / MineRock_Tin / silvervein / MineRock_Obsidian are
    /// plain Destructible nodes; MineRock_Meteorite is MineRock5. Plain MineRock (the older,
    /// per-hit-area component) is not used by any 0.221.12 vanilla ore prefab but is patched too
    /// so a modded ore built on it still gets FastMining.
    ///
    /// HOOKS (verified in the decompile):
    ///   Destructible.RPC_Damage(long, HitData)          - prefix, mutate hit.m_damage.m_pickaxe
    ///   MineRock5.RPC_Damage(long, HitData, int)        - prefix, same
    ///   MineRock.RPC_Hit(long, HitData, int)            - prefix, same
    /// All three are private instance methods registered as the ZNetView RPC handler and only
    /// ever run on the process that owns the node's ZDO (an owning client, or a listen-server
    /// host acting as a client) - never on a dedicated server for a client-owned node. That is why
    /// this module is Side=Client: running the same code on every player's own client keeps the
    /// speed-up consistent for everyone with the mod, exactly like vanilla damage already is.
    ///
    /// DROP MULTIPLIER: all three drop loops - DropOnDestroyed.OnDestroyed for the Destructible
    /// family (invoked synchronously from inside Destructible.Destroy, itself called from inside
    /// RPC_Damage once health reaches 0), the inline loop in MineRock5.DamageArea (called from
    /// RPC_Damage) and the inline loop in MineRock.RPC_Hit - all call the same
    /// DropTable.GetDropList() (the no-arg, List&lt;GameObject&gt; overload) exactly once per
    /// destroyed hit area, and in every case that call happens synchronously inside the very
    /// RPC_Damage/RPC_Hit call this module already prefixes. So DropMultiplier is implemented as
    /// ONE postfix on DropTable.GetDropList() that is normally a complete no-op: the three RPC
    /// prefixes set a "current multiplier" flag only when the node being hit is an allow-listed,
    /// behind-the-frontier ore, and a shared postfix on the same three methods restores it
    /// afterwards - so GetDropList() is only ever touched for the narrow window of an ore-node
    /// hit, never for trees/creatures/containers/anything else in the game. This is narrower than
    /// patching DropOnDestroyed or MineRock5.DamageArea separately and needs only one drop-side
    /// patch instead of three.
    ///
    /// TOOL TIER: HitData.CheckToolTier(int minToolTier, ...), called inline inside all three
    /// original methods, is a simple `if (m_toolTier &lt; minToolTier) ...` comparison. When
    /// IgnoreToolTier is on this module simply raises hit.m_toolTier up to the node's own
    /// m_minToolTier before the original runs, in the same prefix that already has both values.
    /// </summary>
    internal sealed class FastMiningModule : FeatureModule
    {
        public override string Name => "FastMining";

        /// <summary>
        /// Normally Client. [Mining] SelfTest = true (machine-local) flips this to Both so the
        /// headless test server (0 players) can prove the allowlist resolution and the pure
        /// speed-multiplier maths on its own - same trick as FoodNoDecay/VanguardShadow (Plugin.
        /// Awake calls Configure(), which runs Bind(), for every module before TryEnable() reads
        /// Side). The three RPC prefixes and the GetDropList postfix still gate on Live()
        /// (Active + ClientActive()) and stay completely inert on a dedicated server even when
        /// Side is forced to Both for the self-test.
        /// </summary>
        public override ModuleSide Side => _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        public override string Section => "Mining";

        protected override string EnabledDescription =>
            "Mining ore nodes whose material tier is behind the frontier is faster (and " +
            "optionally drops more). Runs on the node-owning client - the same for every player " +
            "who has the mod.";

        /// <summary>
        /// FRACTURED STAGES (0.221.12+, found on Matt's first live test): a copper vein
        /// `rock4_copper` is a Destructible whose m_spawnWhenDestroyed swaps it for
        /// `rock4_copper_frac` on the FIRST pickaxe hit (Destructible.cs:166-168); the frac object
        /// is the MineRock5 you actually mine out. `silvervein` -> `silvervein_frac` likewise. So
        /// without the _frac names in this list only the very first hit of a vein was fast and the
        /// rest was vanilla speed. Both stages are listed, at the same tier.
        /// </summary>
        public const string DefaultOreNodes =
            "rock4_copper:1,rock4_copper_frac:1,MineRock_Tin:1,silvervein:3,silvervein_frac:3," +
            "MineRock_Obsidian:3,MineRock_Meteorite:4";

        private ConfigEntry<float> _speedMult;
        private ConfigEntry<float> _dropMult;
        private ConfigEntry<bool> _ignoreToolTier;
        private ConfigEntry<string> _oreNodes;
        private ConfigEntry<bool> _selfTest;

        private static FastMiningModule _self;

        private readonly Dictionary<int, OrePrefab> _allow = new Dictionary<int, OrePrefab>();
        /// <summary>Prefab hashes already reported at Debug, so the per-node line is logged once.</summary>
        private readonly HashSet<int> _logged = new HashSet<int>();
        private bool _allowResolved;
        private string _allowSummary = "(not resolved yet)";
        private bool _selfTestDone;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        // ---- config -------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _speedMult = BindSynced("SpeedMultiplier", 3.0f,
                "Pickaxe damage multiplier applied to a hit on an ore node whose material tier is " +
                "behind the frontier (see [Frontier] TiersBehind). 1.0 = vanilla speed.");

            _dropMult = BindSynced("DropMultiplier", 1.0f,
                "Extra-drops multiplier for the same behind-the-frontier ore nodes. 1.0 = vanilla " +
                "drop count. 2.0 = always double, 1.5 = 50% chance of one extra full copy of the " +
                "drop list, etc.");

            _ignoreToolTier = BindSynced("IgnoreToolTier", false,
                "Let a pickaxe below the node's required tool tier mine a behind-the-frontier ore " +
                "node anyway (raises the hit's tool tier to the node's own requirement before the " +
                "vanilla tool-tier check runs).");

            _oreNodes = BindSynced("OreNodes", DefaultOreNodes,
                "Ore node prefabs FastMining applies to, as name:tier,name:tier. The tier is the " +
                "material tier checked against the frontier (see [Tiers]/[Frontier]) - it does not " +
                "have to match [Tiers] MaterialTiers, but normally should. Names are resolved " +
                "against ZNetScene's prefab list at runtime; unknown names are logged and ignored.");

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. On next ZNetScene load, logs each " +
                "configured ore prefab's component family (Destructible / MineRock5 / MineRock) " +
                "and m_minToolTier, then runs the pure speed-multiplier function against " +
                "rock4_copper and silvervein with a fake 30-damage hit and logs the result. " +
                "Changes no game state. Leave false in normal use.");
        }

        protected override void ApplyPatches()
        {
            var destructibleDamage = AccessTools.Method(typeof(Destructible), "RPC_Damage",
                new[] { typeof(long), typeof(HitData) });
            if (destructibleDamage == null)
                throw new Exception("Destructible.RPC_Damage(long,HitData) not found");
            Harmony.Patch(destructibleDamage,
                prefix: new HarmonyMethod(typeof(FastMiningModule), nameof(DestructiblePrefix)),
                postfix: new HarmonyMethod(typeof(FastMiningModule), nameof(RestoreDropContext)));

            var mineRock5Damage = AccessTools.Method(typeof(MineRock5), "RPC_Damage",
                new[] { typeof(long), typeof(HitData), typeof(int) });
            if (mineRock5Damage == null)
                throw new Exception("MineRock5.RPC_Damage(long,HitData,int) not found");
            Harmony.Patch(mineRock5Damage,
                prefix: new HarmonyMethod(typeof(FastMiningModule), nameof(MineRock5Prefix)),
                postfix: new HarmonyMethod(typeof(FastMiningModule), nameof(RestoreDropContext)));

            var mineRockHit = AccessTools.Method(typeof(MineRock), "RPC_Hit",
                new[] { typeof(long), typeof(HitData), typeof(int) });
            if (mineRockHit == null)
                throw new Exception("MineRock.RPC_Hit(long,HitData,int) not found");
            Harmony.Patch(mineRockHit,
                prefix: new HarmonyMethod(typeof(FastMiningModule), nameof(MineRockPrefix)),
                postfix: new HarmonyMethod(typeof(FastMiningModule), nameof(RestoreDropContext)));

            var getDropList = AccessTools.Method(typeof(DropTable), "GetDropList", Type.EmptyTypes);
            if (getDropList == null)
                throw new Exception("DropTable.GetDropList() not found");
            Harmony.Patch(getDropList,
                postfix: new HarmonyMethod(typeof(FastMiningModule), nameof(GetDropListPostfix)));

            // ZNetScene doesn't exist yet at plugin Awake; the allowlist is resolved lazily on
            // first use instead (same pattern as OreRegrowthModule.EnsureAllowlist). Only when
            // SelfTest is on do we additionally hook ZNetScene.Awake to run the headless proof
            // once it is populated - never installed in the shipping default (same trick
            // FoodNoDecay uses for ObjectDB.Awake).
            if (_selfTest.Value)
            {
                var znetSceneAwake = AccessTools.Method(typeof(ZNetScene), "Awake");
                if (znetSceneAwake == null)
                    throw new Exception("ZNetScene.Awake() not found");
                Harmony.Patch(znetSceneAwake,
                    postfix: new HarmonyMethod(typeof(FastMiningModule), nameof(ZNetSceneReady)));
            }

            Log.LogInfo("[" + Name + "] SpeedMultiplier=" + _speedMult.Value.ToString("0.##") +
                        "x DropMultiplier=" + _dropMult.Value.ToString("0.##") +
                        "x IgnoreToolTier=" + _ignoreToolTier.Value +
                        " OreNodes=" + _oreNodes.Value + " SelfTest=" + _selfTest.Value);
        }

        public override void Disable()
        {
            base.Disable();
            if (_self == this) _self = null;
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == null) return;
            if (entry.Definition.Key == "OreNodes")
            {
                _allowResolved = false;
                _allow.Clear();
                Log.LogInfo("[" + Name + "] OreNodes changed, allowlist will be re-resolved on next use");
                return;
            }
            if (Active)
                Log.LogInfo("[" + Name + "] SpeedMultiplier=" + _speedMult.Value.ToString("0.##") +
                            "x DropMultiplier=" + _dropMult.Value.ToString("0.##") +
                            "x IgnoreToolTier=" + _ignoreToolTier.Value);
        }

        public override string StatusDetail()
        {
            EnsureAllowlist();
            var qualifying = new List<string>();
            foreach (var kv in _allow)
                if (Tiers.IsBehind(kv.Value.Tier)) qualifying.Add(kv.Value.Name);
            qualifying.Sort(StringComparer.OrdinalIgnoreCase);

            return "SpeedMultiplier=" + _speedMult.Value.ToString("0.##") + "x" +
                   " DropMultiplier=" + _dropMult.Value.ToString("0.##") + "x" +
                   " IgnoreToolTier=" + _ignoreToolTier.Value +
                   " ore=" + _allowSummary +
                   " qualifying=[" + string.Join(",", qualifying.ToArray()) + "]";
        }

        // ---- allowlist ------------------------------------------------------------------------

        private void EnsureAllowlist()
        {
            if (_allowResolved) return;
            if (ZNetScene.instance == null) return;   // not ready yet; try again next call

            _allow.Clear();
            _logged.Clear();
            var resolved = new List<string>();
            var missing = new List<string>();

            var spec = _oreNodes == null ? DefaultOreNodes : _oreNodes.Value;
            foreach (var raw in spec.Split(','))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;

                var parts = s.Split(':');
                var name = parts[0].Trim();
                if (name.Length == 0) continue;

                int tier = 1;
                if (parts.Length > 1 && !int.TryParse(parts[1].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out tier))
                {
                    Log.LogWarning("[" + Name + "] bad tier in OreNodes entry '" + s + "', using 1");
                    tier = 1;
                }

                int hash = name.GetStableHashCode();
                var go = ZNetScene.instance.GetPrefab(hash);
                if (go == null) { missing.Add(name); continue; }

                var family = FamilyOf(go);
                _allow[hash] = new OrePrefab { Hash = hash, Name = name, Tier = tier, Family = family };
                resolved.Add(name + ":" + tier + "(" + family + ")");
            }

            _allowResolved = true;
            _allowSummary = _allow.Count + "/" + (resolved.Count + missing.Count);

            Log.LogInfo("[" + Name + "] ore allowlist resolved: " +
                        (resolved.Count == 0 ? "(none)" : string.Join(", ", resolved.ToArray())));
            if (missing.Count > 0)
                Log.LogWarning("[" + Name + "] OreNodes prefab(s) NOT found in ZNetScene (ignored): " +
                               string.Join(", ", missing.ToArray()));
        }

        /// <summary>Component family + m_minToolTier, for logging (PROOF step 1 in NOTES).</summary>
        private static string FamilyOf(GameObject go)
        {
            var mr5 = go.GetComponentInChildren<MineRock5>(true);
            if (mr5 != null) return "MineRock5,minToolTier=" + mr5.m_minToolTier;
            var mr = go.GetComponentInChildren<MineRock>(true);
            if (mr != null) return "MineRock,minToolTier=" + mr.m_minToolTier;
            var d = go.GetComponentInChildren<Destructible>(true);
            if (d != null) return "Destructible,minToolTier=" + d.m_minToolTier;
            return "unknown-family";
        }

        // ---- damage prefixes --------------------------------------------------------------------

        private static void DestructiblePrefix(Destructible __instance, HitData hit)
        {
            if (!Live() || hit == null) return;
            try
            {
                var nv = __instance.m_nview;
                if (nv == null || !nv.IsValid()) return;
                var zdo = nv.GetZDO();
                if (zdo == null) return;

                _self.EnsureAllowlist();
                if (!_self._allow.TryGetValue(zdo.GetPrefab(), out var info)) return;
                if (!Tiers.IsBehind(info.Tier)) return;

                Apply(hit, __instance.m_minToolTier, info);
            }
            catch (Exception e)
            {
                Log.LogError("[FastMining] Destructible prefix failed: " + e.Message);
            }
        }

        private static void MineRock5Prefix(MineRock5 __instance, HitData hit)
        {
            if (!Live() || hit == null) return;
            try
            {
                var nv = __instance.m_nview;
                if (nv == null || !nv.IsValid()) return;
                var zdo = nv.GetZDO();
                if (zdo == null) return;

                _self.EnsureAllowlist();
                if (!_self._allow.TryGetValue(zdo.GetPrefab(), out var info)) return;
                if (!Tiers.IsBehind(info.Tier)) return;

                Apply(hit, __instance.m_minToolTier, info);
            }
            catch (Exception e)
            {
                Log.LogError("[FastMining] MineRock5 prefix failed: " + e.Message);
            }
        }

        private static void MineRockPrefix(MineRock __instance, HitData hit)
        {
            if (!Live() || hit == null) return;
            try
            {
                var nv = __instance.m_nview;
                if (nv == null || !nv.IsValid()) return;
                var zdo = nv.GetZDO();
                if (zdo == null) return;

                _self.EnsureAllowlist();
                if (!_self._allow.TryGetValue(zdo.GetPrefab(), out var info)) return;
                if (!Tiers.IsBehind(info.Tier)) return;

                Apply(hit, __instance.m_minToolTier, info);
            }
            catch (Exception e)
            {
                Log.LogError("[FastMining] MineRock prefix failed: " + e.Message);
            }
        }

        /// <summary>Shared effect once a hit is confirmed on a behind-the-frontier allow-listed node.</summary>
        private static void Apply(HitData hit, int minToolTier, OrePrefab info)
        {
            // One Debug line per distinct prefab per session: which node family actually reached
            // the multiplier. This is how the fractured-stage bug (only the first hit was fast,
            // because rock4_copper_frac was not allow-listed) shows up in a client log.
            if (info != null && _self._logged.Add(info.Hash))
                Log.LogDebug("[FastMining] hit matched " + info.Name + " tier=" + info.Tier +
                             " family=" + info.Family + " -> pickaxe x" +
                             _self._speedMult.Value.ToString("0.##"));

            float mult = _self._speedMult.Value;
            if (mult > 0f && mult != 1f) hit.m_damage.m_pickaxe *= mult;

            if (_self._ignoreToolTier.Value && hit.m_toolTier < minToolTier)
                hit.m_toolTier = (short)minToolTier;

            float dropMult = _self._dropMult.Value;
            if (dropMult > 1f) DropContext.Multiplier = dropMult;
        }

        private static void RestoreDropContext()
        {
            DropContext.Multiplier = 0f;
        }

        // ---- drop multiplier --------------------------------------------------------------------

        private static void GetDropListPostfix(List<GameObject> __result)
        {
            float mult = DropContext.Multiplier;
            if (mult <= 1f || __result == null || __result.Count == 0) return;

            try
            {
                float extraMult = mult - 1f;
                int wholeCopies = Mathf.FloorToInt(extraMult);
                float frac = extraMult - wholeCopies;

                var original = new List<GameObject>(__result);
                for (int c = 0; c < wholeCopies; c++) __result.AddRange(original);
                if (frac > 0f && UnityEngine.Random.value < frac) __result.AddRange(original);
            }
            catch (Exception e)
            {
                Log.LogError("[FastMining] drop multiplier failed: " + e.Message);
            }
        }

        // ---- self test ----------------------------------------------------------------------------

        private static void ZNetSceneReady(ZNetScene __instance)
        {
            if (_self == null || _self._selfTest == null || !_self._selfTest.Value) return;
            _self.RunSelfTest();
        }

        private void RunSelfTest()
        {
            if (_selfTestDone) return;
            _selfTestDone = true;
            try
            {
                EnsureAllowlist();
                Log.LogInfo("[FastMining][SelfTest] --- begin --- WorldTier=" + Frontier.Describe() +
                            " TiersBehind=" + (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1));

                foreach (var kv in _allow)
                    Log.LogInfo("[FastMining][SelfTest] " + kv.Value.Name + " tier=" + kv.Value.Tier +
                                " family=" + kv.Value.Family);

                ProbeSpeed("rock4_copper", 30f);
                ProbeSpeed("rock4_copper_frac", 30f);   // the stage actually mined out
                ProbeSpeed("silvervein", 30f);
                ProbeSpeed("silvervein_frac", 30f);
                Log.LogInfo("[FastMining][SelfTest] --- end ---");
            }
            catch (Exception e)
            {
                Log.LogError("[FastMining][SelfTest] threw: " + e);
            }
        }

        /// <summary>
        /// Pure function of the allowlist + Tiers.IsBehind, exercised directly (not via a fake RPC
        /// call) so the self test needs no owned ZDO. Shares the exact multiply used by Apply().
        /// </summary>
        private void ProbeSpeed(string name, float baseDamage)
        {
            int hash = name.GetStableHashCode();
            if (!_allow.TryGetValue(hash, out var info))
            {
                Log.LogWarning("[FastMining][SelfTest] " + name + " not in allowlist, skipped");
                return;
            }
            bool behind = Tiers.IsBehind(info.Tier);
            float result = behind ? baseDamage * _speedMult.Value : baseDamage;
            Log.LogInfo("[FastMining][SelfTest] " + name + " tier=" + info.Tier + " IsBehind=" + behind +
                        " pickaxeDamage " + baseDamage.ToString("0.##") + " -> " + result.ToString("0.##"));
        }
    }

    /// <summary>One allow-listed ore prefab.</summary>
    internal sealed class OrePrefab
    {
        public int Hash;
        public string Name;
        public int Tier;
        public string Family;
    }

    /// <summary>
    /// Narrow-scope flag consumed only by FastMiningModule.GetDropListPostfix. 0f (not 1f) is the
    /// "inactive" sentinel so a non-mining GetDropList() call is a single float comparison and
    /// nothing else, even if this were ever read from a different thread than it was written on.
    /// </summary>
    internal static class DropContext
    {
        internal static float Multiplier;
    }
}
