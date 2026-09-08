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
    /// BuildersLoad - near a workbench, building materials weigh half. Fewer trips from the pile
    /// to the wall, and nothing else changes.
    ///
    /// HOOK: a postfix on <c>Inventory.GetTotalWeight()</c>, the single accessor every encumbrance
    /// and weight readout in the game goes through (Player.IsEncumbered, the pickup check, the
    /// inventory header, the radial and throw UIs, TombStone's "can I take it all?" test). The
    /// postfix subtracts the discounted share of the LISTED materials from the number vanilla just
    /// returned; it never writes <c>m_totalWeight</c>, never touches an ItemData, and never runs
    /// for anything but <c>Player.m_localPlayer</c>'s own inventory - the very first thing it does
    /// is compare the instance against <c>Player.m_localPlayer.GetInventory()</c>, so chests,
    /// carts, ships and other players are structurally out of reach.
    ///
    /// RANGE comes from WorkbenchReach's shared helper, so "in range" means exactly what the build
    /// hammer means by it - including the tier/level extension when [Workbench] is on, and plain
    /// vanilla 20 m when it is off. One definition, no second copy of the distance maths.
    ///
    /// CHEAP AND STEADY. GetTotalWeight is called every frame by the HUD, so the answer is cached
    /// twice over:
    ///   - the station scan runs at most once every CheckIntervalSeconds (0.5 s by default);
    ///   - the listed-material weight is recomputed only when the interval elapses OR when
    ///     vanilla's own total changes (which is exactly when the inventory changed), so picking
    ///     up 60 wood is reflected on the very next frame instead of up to half a second later.
    /// HYSTERESIS: once in range the player STAYS "in range" for HysteresisSeconds after walking
    /// out, so the encumbrance arrow cannot flicker while standing on the boundary.
    ///
    /// GUARDRAILS. The carry-weight CAP (<c>Player.GetMaxCarryWeight</c>) is never touched - only
    /// the load side of the comparison. Nothing is stored and nothing is written to a ZDO or a
    /// save: walking out of range simply stops subtracting, so a player who fills up next to the
    /// bench and wanders off becomes encumbered, exactly as intended. Unlisted items are never
    /// affected. This is a purely local convenience: no RPC, no shared state, nothing another
    /// client or the server can observe.
    ///
    /// Side = Client (a dedicated server has no local player). <c>[Load] SelfTest</c> flips it to
    /// Both so a headless server can still resolve the material list against ObjectDB and prove
    /// what every client will do; the weight postfix stays inert there because it gates on
    /// ClientActive() and on there being a local player at all.
    /// </summary>
    internal sealed class BuildersLoadModule : FeatureModule
    {
        public override string Name => "BuildersLoad";
        public override string Section => "Load";

        /// <summary>Client, except while the local SelfTest flag is on - see the class doc.</summary>
        public override ModuleSide Side =>
            _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        protected override string EnabledDescription =>
            "While you are inside a listed crafting station's build range, the listed building " +
            "materials in your own inventory weigh less. Local, cosmetic-to-the-server, and " +
            "reverts the moment you walk away.";

        /// <summary>
        /// Building materials. Every name here was resolved against 0.221.13's ObjectDB on the
        /// test server before shipping (the old "Ancientbark" is not a prefab in this build - the
        /// item is ElderBark - so it was dropped rather than left to log a warning on every boot).
        /// Names that a future game build renames are reported once and ignored.
        /// </summary>
        internal const string DefaultMaterials =
            "Wood,FineWood,RoundLog,Stone,BlackMarble,Grausten,Flint,Resin,Tar,Iron,Copper,Tin," +
            "Bronze,BlackMetal,Chain,Crystal,Thunderstone,YggdrasilWood,ElderBark";

        internal const string DefaultStations = "piece_workbench,piece_stonecutter";

        private static ConfigEntry<float> _weightMultiplier;
        private static ConfigEntry<string> _materialsCfg;
        private static ConfigEntry<string> _stationsCfg;
        private static ConfigEntry<float> _hysteresis;
        private static ConfigEntry<float> _checkInterval;
        private static ConfigEntry<bool> _selfTest;

        private static BuildersLoadModule _self;
        private static bool _reported;

        /// <summary>Station PREFAB names, parsed from [Load] Stations.</summary>
        private static HashSet<string> _stations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Item prefab names as configured (before ObjectDB resolution).</summary>
        private static readonly List<string> _wanted = new List<string>();
        /// <summary>Resolved: the items' shared display names, which is what an ItemData in an
        /// inventory always carries (m_dropPrefab can be null on items restored from a save).</summary>
        private static HashSet<string> _sharedNames = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Resolved: the same items' prefab names, the cheaper first test.</summary>
        private static HashSet<string> _prefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _missing = new List<string>();
        private static bool _resolved;

        // range state (hysteresis)
        private static bool _inRange;
        private static float _lastCheck = -999f;
        private static float _lastInRange = -999f;

        // listed-weight cache
        private static float _cachedListed;
        private static float _cachedForTotal = float.NaN;
        private static float _cachedAt = -999f;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        // ---- config -------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _weightMultiplier = BindSynced("WeightMultiplier", 0.5f,
                "What a listed building material weighs while you are in range of a listed " +
                "station, as a fraction of normal. 0.5 = half. Clamped to 0.01..1: this module " +
                "never makes anything heavier.");

            _materialsCfg = BindSynced("Materials", DefaultMaterials,
                "Comma-separated item PREFAB names this applies to. Building materials only - " +
                "food, ore that still has to be smelted and everything else stays at full " +
                "weight. Names that do not exist in this game build are logged once and ignored.");

            _stationsCfg = BindSynced("Stations", DefaultStations,
                "Comma-separated crafting-station PREFAB names whose build range counts as " +
                "\"near a bench\". The range used is the same one the build hammer uses, so it " +
                "grows with [Workbench] WorkbenchReach when that module is on.");

            _hysteresis = BindSynced("HysteresisSeconds", 3f,
                "How long you keep the discount after leaving a station's range, so the " +
                "encumbrance arrow cannot flicker while you stand on the boundary. 0 = snap " +
                "back instantly.");

            _checkInterval = BindSynced("CheckIntervalSeconds", 0.5f,
                "How often the \"am I near a bench?\" test actually runs. The answer is cached " +
                "in between, because the weight is read every frame. Clamped to 0.05..5.");

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Flips this module's Side to Both so a " +
                "dedicated server can resolve the material list against ObjectDB and log which " +
                "names were found and which were not, once per world load. Changes no game " +
                "state. Leave false in normal use.");

            ParseLists();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _materialsCfg || entry == _stationsCfg)
            {
                ParseLists();
                _resolved = false;
                Invalidate();
                EnsureResolved();
            }
            Invalidate();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override void Disable()
        {
            base.Disable();
            if (_self == this) _self = null;
        }

        private static void Invalidate()
        {
            _cachedForTotal = float.NaN;
            _lastCheck = -999f;
        }

        private static void ParseLists()
        {
            _wanted.Clear();
            foreach (var name in Split(_materialsCfg, DefaultMaterials)) _wanted.Add(name);

            var stations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Split(_stationsCfg, DefaultStations)) stations.Add(name);
            _stations = stations;
        }

        private static IEnumerable<string> Split(ConfigEntry<string> entry, string fallback)
        {
            var raw = entry != null ? entry.Value : fallback;
            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = chunk.Trim();
                if (s.Length > 0) yield return s;
            }
        }

        // ---- patches ------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var total = AccessTools.Method(typeof(Inventory), "GetTotalWeight");
            if (total == null) throw new Exception("Inventory.GetTotalWeight() not found");
            Harmony.Patch(total, postfix: new HarmonyMethod(typeof(BuildersLoadModule), nameof(TotalWeightPost)));

            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(BuildersLoadModule), nameof(WorldReady)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        /// <summary>The one place a weight changes. Never writes m_totalWeight; only the value the
        /// caller is about to use.</summary>
        private static void TotalWeightPost(Inventory __instance, ref float __result)
        {
            if (!Live() || __result <= 0f || __instance == null) return;
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                if (!ReferenceEquals(player.GetInventory(), __instance)) return;   // never a chest

                float mult = Multiplier();
                if (mult >= 1f) return;
                if (!InRange(player)) return;

                float listed = ListedWeight(__instance, __result);
                if (listed <= 0f) return;

                float lighter = __result - listed * (1f - mult);
                __result = lighter < 0f ? 0f : lighter;
            }
            catch (Exception e)
            {
                Log.LogError("[BuildersLoad] weight postfix failed: " + e.Message);
            }
        }

        private static float Multiplier()
        {
            float m = _weightMultiplier != null ? _weightMultiplier.Value : 1f;
            if (float.IsNaN(m)) return 1f;
            return Mathf.Clamp(m, 0.01f, 1f);
        }

        /// <summary>Station scan, throttled, with hysteresis on the way out.</summary>
        private static bool InRange(Player player)
        {
            float now = Time.time;
            float interval = Mathf.Clamp(_checkInterval != null ? _checkInterval.Value : 0.5f, 0.05f, 5f);
            if (now - _lastCheck < interval) return _inRange;

            _lastCheck = now;
            bool near = WorkbenchReachModule.NearStation(player.transform.position, _stations);
            if (near)
            {
                _inRange = true;
                _lastInRange = now;
                return true;
            }

            float hold = _hysteresis != null ? Mathf.Max(0f, _hysteresis.Value) : 0f;
            if (_inRange && now - _lastInRange <= hold) return true;

            _inRange = false;
            return false;
        }

        /// <summary>Total weight of the LISTED materials in this inventory. Recomputed when the
        /// throttle elapses or when vanilla's own total moved (i.e. the inventory changed).</summary>
        private static float ListedWeight(Inventory inv, float vanillaTotal)
        {
            float now = Time.time;
            float interval = Mathf.Clamp(_checkInterval != null ? _checkInterval.Value : 0.5f, 0.05f, 5f);
            if (vanillaTotal == _cachedForTotal && now - _cachedAt < interval) return _cachedListed;

            EnsureResolved();

            float sum = 0f;
            var items = inv.GetAllItems();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null || item.m_shared == null) continue;
                    if (!IsListed(item)) continue;
                    sum += item.GetWeight();
                }
            }

            _cachedListed = sum;
            _cachedForTotal = vanillaTotal;
            _cachedAt = now;
            return sum;
        }

        private static bool IsListed(ItemDrop.ItemData item)
        {
            if (item.m_dropPrefab != null &&
                _prefabNames.Contains(Utils.GetPrefabName(item.m_dropPrefab.name))) return true;
            return _sharedNames.Contains(item.m_shared.m_name);
        }

        // ---- resolving the material list against ObjectDB -----------------------------------------

        private static void EnsureResolved()
        {
            if (_resolved) return;
            var odb = ObjectDB.instance;
            if (odb == null) return;   // not ready; try again on the next call

            var shared = new HashSet<string>(StringComparer.Ordinal);
            var prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _missing.Clear();

            foreach (var name in _wanted)
            {
                GameObject go = null;
                try { go = odb.GetItemPrefab(name); }
                catch { /* treated as missing below */ }

                var drop = go == null ? null : go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null)
                {
                    _missing.Add(name);
                    continue;
                }
                prefabs.Add(Utils.GetPrefabName(go));
                shared.Add(drop.m_itemData.m_shared.m_name);
            }

            _prefabNames = prefabs;
            _sharedNames = shared;
            _resolved = true;
        }

        // ---- reporting -------------------------------------------------------------------------------

        private string Numbers()
        {
            return "WeightMultiplier=x" + Multiplier().ToString("0.##", CultureInfo.InvariantCulture) +
                   " stations=[" + string.Join(",", Sorted(_stations)) + "]" +
                   " hysteresis=" + (_hysteresis != null ? _hysteresis.Value : 0f).ToString("0.#", CultureInfo.InvariantCulture) + "s" +
                   " check=" + (_checkInterval != null ? _checkInterval.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture) + "s" +
                   " materials=" + MaterialsSummary() +
                   " (local player only; carry-weight cap unchanged)";
        }

        private static string MaterialsSummary()
        {
            if (!_resolved) return _wanted.Count + " configured (not resolved yet)";
            return _prefabNames.Count + "/" + _wanted.Count + " found" +
                   (_missing.Count > 0 ? ", missing: " + string.Join(",", _missing.ToArray()) : "");
        }

        private static string[] Sorted(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        public override string StatusDetail() { return Numbers(); }

        private static void WorldReady()
        {
            if (_reported || _self == null || !_self.Active) return;
            _reported = true;
            try
            {
                EnsureResolved();
                Log.LogInfo("[BuildersLoad] " + _self.Numbers());
                if (_missing.Count > 0)
                    Log.LogWarning("[BuildersLoad] Materials not in ObjectDB (ignored): " +
                                   string.Join(", ", _missing.ToArray()));
                if (_selfTest != null && _selfTest.Value)
                    foreach (var line in SelfTest().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[BuildersLoad] world-ready report failed: " + e);
            }
        }

        /// <summary>Every configured material with its real per-unit weight, before and after.</summary>
        internal static string SelfTest()
        {
            EnsureResolved();

            var sb = new StringBuilder();
            float mult = Multiplier();
            sb.Append("[SelfTest][BuildersLoad] WeightMultiplier=x")
              .Append(mult.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" stations=[").Append(string.Join(",", Sorted(_stations))).Append(']')
              .Append(" hysteresis=")
              .Append((_hysteresis != null ? _hysteresis.Value : 0f).ToString("0.#", CultureInfo.InvariantCulture))
              .Append("s  materials ").Append(MaterialsSummary());

            var odb = ObjectDB.instance;
            if (odb == null)
            {
                sb.Append("\n  ObjectDB not ready - cannot resolve any material");
                return sb.ToString();
            }

            int stack60 = 0;
            foreach (var name in _wanted)
            {
                GameObject go = null;
                try { go = odb.GetItemPrefab(name); }
                catch { }
                var drop = go == null ? null : go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null)
                {
                    sb.Append("\n  ").Append(name).Append(": NOT in ObjectDB - dropped from the list");
                    continue;
                }

                float w = drop.m_itemData.m_shared.m_weight;
                sb.Append("\n  ").Append(name.PadRight(16))
                  .Append("weight ").Append(w.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append(" -> ").Append((w * mult).ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("   (60 carried: ").Append((w * 60f).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(" -> ").Append((w * 60f * mult).ToString("0.#", CultureInfo.InvariantCulture)).Append(')');
                stack60++;
            }

            sb.Append("\n  ").Append(stack60).Append(" material(s) resolved; ")
              .Append(_missing.Count).Append(" missing. Applies to Player.m_localPlayer's own ")
              .Append("inventory only - chests, carts and other players are never touched.");
            return sb.ToString();
        }
    }
}
