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
    /// WorkbenchReach - a workbench's build range grows a little with every boss the world has
    /// killed, and a lot with every level of extensions, so one well-upgraded bench can cover
    /// nearly a whole base.
    ///
    ///     effective range = vanilla GetStationBuildRange()
    ///                     + PerTierMetres  * Frontier.WorldTier
    ///                     + PerLevelMetres * (station level - 1)
    ///     capped at MaxRangeMetres, and never below vanilla.
    ///
    /// "vanilla GetStationBuildRange()" is the station's own <c>m_buildRange</c>, which vanilla
    /// recomputes every 2 s inside <c>CraftingStation.GetExtensions()</c> as
    /// <c>m_rangeBuild + extensionCount * m_extraRangePerLevel</c> - so any change another mod
    /// makes to either field is respected and simply extended.
    ///
    /// ONE HOOK FOR EVERY RANGE CHECK. In the 0.221.13 decompile <c>m_rangeBuild</c> is read in
    /// exactly one place, <c>GetExtensions()</c>, and the resulting <c>m_buildRange</c> is exposed
    /// through exactly one accessor, <c>CraftingStation.GetStationBuildRange()</c>, whose only
    /// caller is the static <c>HaveBuildStationInRange(name, point)</c>. That single static is in
    /// turn what every "is a workbench near enough?" decision in the game goes through:
    ///
    ///   Player.HaveRequirements(Piece, RequirementMode)   piece placement validation (Player.cs:2634)
    ///   Player.CheckCanRemovePiece(Piece)                 deconstruct AND hammer repair
    ///                                                     (Player.cs:2714; Player.Repair calls it)
    ///   Hud.SetupPieceInfo(Piece)                         the build HUD's "workbench" requirement row
    ///                                                     (Hud.cs:1419)
    ///   CraftFromChestsModule                             its own build-station check calls the
    ///                                                     same static, so it follows automatically
    ///
    /// So a single postfix on <c>GetStationBuildRange()</c> moves all of them together and there is
    /// no way for the HUD, the placement check and the consume path to disagree.
    ///
    /// <c>m_rangeBuild</c> IS NEVER MUTATED. It lives on the shared prefab component, so writing to
    /// it would change every workbench in the world at once (and leak into any other mod reading
    /// it). The postfix only rewrites the RETURN VALUE, per instance, per call - nothing is stored.
    ///
    /// THE VISIBLE CIRCLE. <c>CraftingStation.ShowAreaMarker()</c> is what the build HUD calls to
    /// flash the range circle when you hover with the hammer; the circle's radius is a
    /// <c>CircleProjector.m_radius</c> that vanilla sets from <c>m_buildRange</c> inside
    /// <c>GetExtensions()</c>. A postfix on ShowAreaMarker sets it to the EXTENDED range instead,
    /// so the player sees the reach they actually have. ShowAreaMarker is called every frame while
    /// the hammer is out, so the 2 s GetExtensions tick can never leave the circle stale for more
    /// than a frame. This is display only - it writes to the marker object under this one station
    /// instance, never to a prefab.
    ///
    /// WHAT IS DELIBERATELY NOT TOUCHED: <c>m_effectAreaCollider</c>. Vanilla sizes that collider
    /// from <c>m_buildRange</c> in the same GetExtensions block, and it is the EffectArea.PlayerBase
    /// volume that suppresses monster spawns. Because this module never writes <c>m_buildRange</c>,
    /// the spawn-suppression area stays exactly vanilla. Growing it is a different feature and is
    /// explicitly out of scope for this release. <c>m_useDistance</c> (walk-up-and-use range) and
    /// <c>m_discoverRange</c> are untouched too.
    ///
    /// BOTH ENDS RUN THE SAME FORMULA. World tier is replicated (global keys), the station's level
    /// is derived from its own attached extensions, and the config is server-synced - so a client's
    /// placement validation and a server's view of the same station agree by construction.
    /// </summary>
    internal sealed class WorkbenchReachModule : FeatureModule
    {
        public override string Name => "WorkbenchReach";
        public override string Section => "Workbench";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Theme => "Building & gathering";
        public override string Hint => "How far a workbench reaches, grows with bosses and upgrades";

        protected override string EnabledDescription =>
            "Workbench build range grows with the world's boss progress and with the bench's own " +
            "level, up to a hard cap. Placement, deconstruct, repair, the build HUD and the " +
            "visible range circle all move together. The monster-spawn suppression area is NOT " +
            "changed.";

        private static ConfigEntry<float> _perTier;
        private static ConfigEntry<float> _perLevel;
        private static ConfigEntry<float> _maxRange;
        private static ConfigEntry<string> _stationsCfg;
        private static ConfigEntry<bool> _selfTest;

        private static WorkbenchReachModule _self;
        private static bool _reported;

        /// <summary>Station PREFAB names this module extends, parsed from [Workbench] Stations.</summary>
        private static HashSet<string> _stations =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>CraftingStation.m_allStations, resolved once. Used by the shared range helper
        /// (BuildersLoad) - deliberately resolved lazily rather than at ApplyPatches, so the helper
        /// works even when this module is disabled and never patched anything.</summary>
        private static List<CraftingStation> _allStations;
        private static bool _allStationsResolved;

        private static bool Live()
        {
            return _self != null && _self.Active;
        }

        // ---- config -------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _perTier = BindSynced("PerTierMetres", 2f,
                "Extra build range, in metres, for every boss the world has killed " +
                "([Frontier] WorldTier). 2 = +2 m per boss, so a world that has cleared all " +
                "seven gets +14 m. 0 = no progress bonus.",
                Opt.N("Extra workbench range per world boss killed", 0, 15, 0.5));

            _perLevel = BindSynced("PerLevelMetres", 6f,
                "Extra build range, in metres, per level of the bench above 1 - a level is one " +
                "attached station extension (chopping block, tanning rack, ...). 6 = a level-5 " +
                "bench gets +24 m. This is on top of vanilla's own m_extraRangePerLevel, which " +
                "is 0 on the vanilla workbench.",
                Opt.N("Extra workbench range per bench upgrade level", 0, 40, 1));

            _maxRange = BindSynced("MaxRangeMetres", 60f,
                "Hard cap on the effective build range in metres, whatever the tier and level add " +
                "up to. The result is also never below the vanilla range.",
                Opt.N("Highest possible workbench build range, in metres", 10, 200, 5));

            _stationsCfg = BindSynced("Stations", DefaultStations,
                "Comma-separated crafting-station PREFAB names this applies to. Default: the " +
                "workbench only. Add piece_stonecutter, forge, piece_artisanstation etc. to " +
                "extend those too. Unknown names are simply never matched.",
                Opt.T("Which crafting stations get the extended build range")
                    .Pick(new PickerSpec(PickerSource.Stations)));

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Once per world load, log each configured " +
                "station's vanilla m_rangeBuild / m_extraRangePerLevel read from the real prefab " +
                "and the effective range this module computes at levels 1, 3 and 5 for the " +
                "current world tier. Changes no game state. Leave false in normal use.",
                Opt.B("Log workbench range numbers for every configured station").Admin());

            ParseStations();
        }

        internal const string DefaultStations = "piece_workbench";

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _stationsCfg) ParseStations();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override void Disable()
        {
            base.Disable();
            if (_self == this) _self = null;
        }

        private static void ParseStations()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = _stationsCfg != null ? _stationsCfg.Value : DefaultStations;
            foreach (var chunk in (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = chunk.Trim();
                if (name.Length > 0) set.Add(name);
            }
            _stations = set;
        }

        // ---- patches ------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var range = AccessTools.Method(typeof(CraftingStation), "GetStationBuildRange");
            if (range == null) throw new Exception("CraftingStation.GetStationBuildRange() not found");
            Harmony.Patch(range, postfix: new HarmonyMethod(typeof(WorkbenchReachModule), nameof(BuildRangePost)));

            var marker = AccessTools.Method(typeof(CraftingStation), "ShowAreaMarker");
            if (marker == null) throw new Exception("CraftingStation.ShowAreaMarker() not found");
            if (AccessTools.Field(typeof(CraftingStation), "m_areaMarkerCircle") == null)
                throw new Exception("CraftingStation.m_areaMarkerCircle not found");
            Harmony.Patch(marker, postfix: new HarmonyMethod(typeof(WorkbenchReachModule), nameof(ShowAreaMarkerPost)));

            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(WorkbenchReachModule), nameof(WorldReady)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        /// <summary>The one place the range changes. __result is vanilla's m_buildRange; nothing is
        /// written back to the station or its prefab.</summary>
        private static void BuildRangePost(CraftingStation __instance, ref float __result)
        {
            if (!Live() || __instance == null) return;
            try
            {
                __result = Extend(__instance, __result);
            }
            catch (Exception e)
            {
                Log.LogError("[WorkbenchReach] range postfix failed: " + e.Message);
            }
        }

        /// <summary>Make the circle the player sees match the range they actually have.</summary>
        private static void ShowAreaMarkerPost(CraftingStation __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                var circle = __instance.m_areaMarkerCircle;
                if (circle == null) return;
                if (!Matches(__instance)) return;
                // GetStationBuildRange() is itself patched above, so this is the extended value;
                // calling it here (rather than caching) also refreshes vanilla's extension list.
                circle.m_radius = __instance.GetStationBuildRange();
            }
            catch (Exception e)
            {
                Log.LogError("[WorkbenchReach] area-marker postfix failed: " + e.Message);
            }
        }

        // ---- the formula ------------------------------------------------------------------------

        private static bool Matches(CraftingStation station)
        {
            if (station == null || _stations.Count == 0) return false;
            var go = station.gameObject;
            if (go == null) return false;
            return _stations.Contains(Utils.GetPrefabName(go));
        }

        /// <summary>vanilla range -> effective range for this station instance.</summary>
        private static float Extend(CraftingStation station, float vanilla)
        {
            if (!Matches(station)) return vanilla;
            // GetLevel(false) reads the extension list vanilla has just refreshed inside
            // GetStationBuildRange(), so this neither re-scans nor recurses.
            return RangeFor(vanilla, station.GetLevel(false), Frontier.WorldTier);
        }

        /// <summary>The pure formula, shared with the self-test table.</summary>
        internal static float RangeFor(float vanilla, int level, int worldTier)
        {
            float perTier = _perTier != null ? _perTier.Value : 0f;
            float perLevel = _perLevel != null ? _perLevel.Value : 0f;
            float cap = _maxRange != null ? _maxRange.Value : vanilla;

            if (float.IsNaN(perTier)) perTier = 0f;
            if (float.IsNaN(perLevel)) perLevel = 0f;

            if (level < 1) level = 1;
            if (worldTier < 0) worldTier = 0;

            float r = vanilla + perTier * worldTier + perLevel * (level - 1);
            if (!float.IsNaN(cap) && r > cap) r = cap;
            if (r < vanilla) r = vanilla;   // never shorter than vanilla
            return r;
        }

        // ---- shared range helper (used by BuildersLoad) -------------------------------------------

        /// <summary>
        /// Is <paramref name="point"/> inside the (WorkbenchReach-extended) build range of any
        /// loaded station whose prefab name is in <paramref name="prefabNames"/>?
        ///
        /// Mirrors vanilla <c>CraftingStation.HaveBuildStationInRange</c> exactly - same station
        /// list, same "flatten the test point to the station's own Y" horizontal distance - but
        /// matches on the station's PREFAB name instead of its localised m_name, which is what
        /// every config entry in this mod uses. Because it goes through
        /// <c>GetStationBuildRange()</c> it returns the extended range when this module is on and
        /// the plain vanilla range when it is off, with no second copy of the formula.
        /// </summary>
        internal static bool NearStation(Vector3 point, HashSet<string> prefabNames)
        {
            if (prefabNames == null || prefabNames.Count == 0) return false;

            var all = AllStations();
            if (all == null) return false;

            for (int i = 0; i < all.Count; i++)
            {
                var st = all[i];
                if (st == null) continue;
                var go = st.gameObject;
                if (go == null) continue;
                if (!prefabNames.Contains(Utils.GetPrefabName(go))) continue;

                var stationPos = st.transform.position;
                var p = point;
                p.y = stationPos.y;
                if (Vector3.Distance(stationPos, p) < st.GetStationBuildRange()) return true;
            }
            return false;
        }

        /// <summary>Vanilla's own list of loaded crafting stations, resolved once. Shared with
        /// BuildersLoad's range test and BuildersGuild's yard scan.</summary>
        internal static List<CraftingStation> AllStations()
        {
            if (_allStationsResolved) return _allStations;
            _allStationsResolved = true;
            try
            {
                _allStations = AccessTools.StaticFieldRefAccess<List<CraftingStation>>(
                    typeof(CraftingStation), "m_allStations");
            }
            catch (Exception e)
            {
                Log.LogError("[WorkbenchReach] CraftingStation.m_allStations not readable: " + e.Message);
                _allStations = null;
            }
            return _allStations;
        }

        // ---- reporting -----------------------------------------------------------------------------

        private string Numbers()
        {
            return "range = vanilla + " +
                   _perTier.Value.ToString("0.##", CultureInfo.InvariantCulture) + "m * tier + " +
                   _perLevel.Value.ToString("0.##", CultureInfo.InvariantCulture) + "m * (level-1)" +
                   ", cap " + _maxRange.Value.ToString("0.##", CultureInfo.InvariantCulture) + "m" +
                   "; WorldTier=" + Frontier.Describe() +
                   " stations=[" + string.Join(",", Sorted()) + "]" +
                   " (spawn-suppression area unchanged)";
        }

        private static string[] Sorted()
        {
            var list = new List<string>(_stations);
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
                foreach (var line in Report().Split('\n')) Log.LogInfo(line);
                if (_selfTest != null && _selfTest.Value)
                    foreach (var line in SelfTest().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[WorkbenchReach] world-ready report failed: " + e);
            }
        }

        /// <summary>The summary line, with the real numbers for the world that just loaded.</summary>
        internal static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("[WorkbenchReach] ").Append(_self != null ? _self.Numbers() : "(not configured)");

            var scene = ZNetScene.instance;
            if (scene == null) return sb.ToString();

            foreach (var name in Sorted())
            {
                float vanilla, extraPerLevel;
                if (!PrefabRange(scene, name, out vanilla, out extraPerLevel))
                {
                    sb.Append("\n  ").Append(name).Append(": not a CraftingStation prefab in ZNetScene");
                    continue;
                }
                int tier = Frontier.WorldTier;
                sb.Append("\n  ").Append(name)
                  .Append(": vanilla m_rangeBuild=").Append(vanilla.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("m extraPerLevel=").Append(extraPerLevel.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("m -> tier ").Append(tier)
                  .Append(" level 1 = ").Append(RangeFor(vanilla, 1, tier).ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("m, level 3 = ").Append(RangeFor(vanilla + 2 * extraPerLevel, 3, tier).ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("m, level 5 = ").Append(RangeFor(vanilla + 4 * extraPerLevel, 5, tier).ToString("0.##", CultureInfo.InvariantCulture))
                  .Append('m');
            }
            return sb.ToString();
        }

        /// <summary>The full table: every configured station at levels 1-7 and world tiers 0-7.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][WorkbenchReach] PerTierMetres=")
              .Append((_perTier != null ? _perTier.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" PerLevelMetres=")
              .Append((_perLevel != null ? _perLevel.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" MaxRangeMetres=")
              .Append((_maxRange != null ? _maxRange.Value : 0f).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(" WorldTier=").Append(Frontier.Describe());

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                sb.Append("\n  ZNetScene has no prefabs yet - cannot read any station");
                return sb.ToString();
            }

            foreach (var name in Sorted())
            {
                float vanilla, extraPerLevel;
                if (!PrefabRange(scene, name, out vanilla, out extraPerLevel))
                {
                    sb.Append("\n  ").Append(name).Append(": not a CraftingStation prefab in ZNetScene");
                    continue;
                }

                sb.Append("\n  ").Append(name).Append(" (vanilla ")
                  .Append(vanilla.ToString("0.##", CultureInfo.InvariantCulture)).Append("m)");
                sb.Append("\n    tier |");
                for (int level = 1; level <= 7; level++) sb.Append(("lvl" + level).PadLeft(8));
                for (int tier = 0; tier <= Frontier.MaxTier; tier++)
                {
                    sb.Append("\n    ").Append(tier.ToString().PadLeft(4)).Append(" |");
                    for (int level = 1; level <= 7; level++)
                    {
                        float v = vanilla + (level - 1) * extraPerLevel;
                        sb.Append((RangeFor(v, level, tier).ToString("0.#", CultureInfo.InvariantCulture) + "m").PadLeft(8));
                    }
                }
            }
            return sb.ToString();
        }

        private static bool PrefabRange(ZNetScene scene, string name, out float rangeBuild, out float extraPerLevel)
        {
            rangeBuild = 0f;
            extraPerLevel = 0f;
            var go = scene.GetPrefab(name.GetStableHashCode());
            if (go == null) return false;
            var st = go.GetComponentInChildren<CraftingStation>(true);
            if (st == null) return false;
            rangeBuild = st.m_rangeBuild;
            extraPerLevel = st.m_extraRangePerLevel;
            return true;
        }
    }
}
