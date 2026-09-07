using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Headless proof for CraftFromChests.
    ///
    /// CraftFromChests is side Client, so on a dedicated server it correctly reports
    /// disabled(side) and patches nothing - which makes it unprovable by a boot alone. But its two
    /// load-bearing decisions are pure: (a) which station families are allowed to pull, and
    /// (b) the counting/consuming arithmetic over a set of inventories. Both can be exercised with
    /// nothing but ObjectDB, ZNetScene and the synced config, all of which the dedicated server
    /// has, and Plugin.Configure() binds every module's config regardless of side.
    ///
    /// With [Chests] SelfTest = true (machine-local, default false, never synced) this runs once
    /// after ZoneSystem.Start and writes the answers to the log. It patches nothing else, touches
    /// no ZDO and changes no game state: the inventories it drives are objects it made itself.
    /// </summary>
    internal sealed class ChestsSelfTestModule : FeatureModule
    {
        public override string Name => "ChestsSelfTest";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "ChestsSelfTest";

        private static ConfigEntry<bool> _selfTest;
        private static bool _ran;

        protected override void Bind()
        {
            _selfTest = BindLocal("Chests", "SelfTest", false,
                "Diagnostic. Once per world load, log the CraftFromChests station toggles, which " +
                "CookingStation prefabs this build actually has, and a unit run of the " +
                "counting/consuming core against inventories this module creates itself. " +
                "Machine-local and never synced, so turning it on for a server boot affects no " +
                "client. Leave it false in normal use.");
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(ChestsSelfTestModule), nameof(WorldReady)));
        }

        private static void WorldReady()
        {
            if (_ran || _selfTest == null || !_selfTest.Value) return;
            _ran = true;
            try
            {
                foreach (var line in Run().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[ChestsSelfTest] threw: " + e);
            }
        }

        internal static string Run()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][Chests] --- begin ---");
            sb.Append("\n  toggles: ").Append(CraftFromChestsModule.Numbers());

            // ---- (1) which prefabs are cooking stations, and where do they land? ----------------
            sb.Append("\n  PullForCookingStations=").Append(CraftFromChestsModule.PullCooking)
              .Append(" -> cooking stations take the ")
              .Append(CraftFromChestsModule.PullCooking ? "PULL" : "NO-PULL")
              .Append(" path (both the cookable item and the station's own fuel)");

            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null)
            {
                sb.Append("\n  ZNetScene has no prefabs - cannot enumerate cooking stations");
            }
            else
            {
                var found = new List<string>();
                foreach (var go in scene.m_prefabs)
                {
                    if (go == null) continue;
                    var cs = go.GetComponent<CookingStation>();
                    if (cs == null) continue;
                    string fuel = cs.m_fuelItem != null ? cs.m_fuelItem.name : "none";
                    found.Add(go.name + " (fuel=" + fuel + ", conversions=" +
                              (cs.m_conversion != null ? cs.m_conversion.Count : 0) + ")");
                }
                sb.Append("\n  CookingStation prefabs in ZNetScene: ").Append(found.Count);
                foreach (var f in found)
                    sb.Append("\n    ").Append(f).Append(" -> ")
                      .Append(CraftFromChestsModule.PullCooking ? "pull" : "NO PULL (default)");

                foreach (var want in new[] { "piece_cookingstation", "piece_cookingstation_iron", "piece_oven" })
                {
                    var go = scene.GetPrefab(want);
                    bool isCs = go != null && go.GetComponent<CookingStation>() != null;
                    sb.Append("\n    expected ").Append(want).Append(": ")
                      .Append(go == null ? "MISSING from ZNetScene"
                                         : (isCs ? "present, CookingStation -> gated by PullForCookingStations"
                                                 : "present but NOT a CookingStation"));
                }

                // The smelter family and the fires must NOT be caught by the cooking gate.
                int smelters = 0, fires = 0;
                foreach (var go in scene.m_prefabs)
                {
                    if (go == null) continue;
                    if (go.GetComponent<Smelter>() != null) smelters++;
                    if (go.GetComponent<Fireplace>() != null) fires++;
                }
                sb.Append("\n  for contrast: ").Append(smelters).Append(" Smelter prefabs (PullForSmelters) and ")
                  .Append(fires).Append(" Fireplace prefabs (PullForFires)");
            }

            // ---- (2) unit-drive the counting/consuming core --------------------------------------
            sb.Append("\n  core: ").Append(CoreTest());

            sb.Append("\n[SelfTest][Chests] --- end ---");
            return sb.ToString();
        }

        private static string CoreTest()
        {
            var odb = ObjectDB.instance;
            if (odb == null) return "ObjectDB not ready - skipped";

            var wood = odb.GetItemPrefab("Wood");
            var meat = odb.GetItemPrefab("RawMeat");
            if (wood == null) return "no Wood prefab in ObjectDB - skipped";

            string woodName = wood.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            string meatName = meat != null ? meat.GetComponent<ItemDrop>().m_itemData.m_shared.m_name : null;

            // Save and restore the real config-driven flag: this is a unit test, not a setting.
            bool savedLeaveOne = ChestSource.LeaveOne;
            var sb = new StringBuilder();
            try
            {
                ChestSource.LeaveOne = false;

                var player = new Inventory("selftest-player", null, 4, 4);
                var chest = new Inventory("selftest-chest", null, 4, 4);
                player.AddItem(wood, 10);
                chest.AddItem(wood, 20);
                if (meat != null) chest.AddItem(meat, 6);

                var boxes = new List<Box> { Box.Of(chest, "selftest-chest") };

                int invCount = player.CountItems(woodName);
                int boxCount = ChestSource.Count(woodName, boxes);
                sb.Append("Count(Wood) player=").Append(invCount)
                  .Append(" containers=").Append(boxCount)
                  .Append(" total=").Append(invCount + boxCount);
                if (meatName != null)
                    sb.Append("; Count(RawMeat) containers=").Append(ChestSource.Count(meatName, boxes));

                // Exactly what ConsumePre/ConsumePost do: vanilla drains the player, we owe the rest.
                const int need = 15;
                int fromPlayer = Mathf.Min(need, player.CountItems(woodName));
                player.RemoveItem(woodName, fromPlayer);
                int owed = need - fromPlayer;
                int got = ChestSource.Consume(woodName, owed, -1, boxes);
                sb.Append("\n         Consume(Wood,15): player gave ").Append(fromPlayer)
                  .Append(", containers gave ").Append(got)
                  .Append(" -> player=").Append(player.CountItems(woodName))
                  .Append(" container=").Append(chest.CountItems(woodName))
                  .Append(fromPlayer == 10 && got == 5 && player.CountItems(woodName) == 0 &&
                          chest.CountItems(woodName) == 15 ? "  PASS (expected 0 / 15)" : "  FAIL");

                // LeaveOneItem: a container must never be emptied to zero.
                ChestSource.LeaveOne = true;
                var chest2 = new Inventory("selftest-chest2", null, 4, 4);
                chest2.AddItem(wood, 5);
                var boxes2 = new List<Box> { Box.Of(chest2, "selftest-chest2") };
                int visible = ChestSource.Count(woodName, boxes2);
                int taken = ChestSource.Consume(woodName, 10, -1, boxes2);
                sb.Append("\n         LeaveOneItem=true, container has 5: Count=").Append(visible)
                  .Append(", Consume(Wood,10) took ").Append(taken)
                  .Append(" -> container=").Append(chest2.CountItems(woodName))
                  .Append(visible == 4 && taken == 4 && chest2.CountItems(woodName) == 1
                              ? "  PASS (keeps 1)" : "  FAIL");
            }
            catch (Exception e)
            {
                sb.Append("  EXCEPTION: ").Append(e.Message);
            }
            finally
            {
                ChestSource.LeaveOne = savedLeaveOne;
            }
            return sb.ToString();
        }

        public override string StatusDetail()
        {
            return "SelfTest=" + (_selfTest != null && _selfTest.Value) + (_ran ? " (already run)" : "");
        }
    }
}
