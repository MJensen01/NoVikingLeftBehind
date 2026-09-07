using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Headless proof for the three client-side Economy modules.
    ///
    /// TrailingTierDiscount, RichSmelting and TraderStock all run on a player's PC, so on a
    /// dedicated server they correctly report disabled(side) and never patch anything. That makes
    /// them unprovable by a server boot alone - but their DECISIONS are pure functions of
    /// ObjectDB, ZNetScene, the frontier and the synced config, all of which the dedicated server
    /// has. Plugin.Configure() runs for every module regardless of side, so their config entries
    /// exist here too.
    ///
    /// With [Economy] SelfTest = true (machine-local, default false, never synced) this module
    /// calls each of their internal SelfTest() methods once, after ZoneSystem.Start has run and
    /// the global keys are loaded, and writes the answers to the log. It patches nothing else and
    /// changes no game state.
    /// </summary>
    internal sealed class EconomySelfTestModule : FeatureModule
    {
        public override string Name => "EconomySelfTest";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "Economy";

        private static ConfigEntry<bool> _selfTest;
        private static bool _ran;

        protected override void Bind()
        {
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic. Log what the Economy modules (TrailingTierDiscount, RichSmelting, " +
                "TraderStock) WOULD do with the current world tier, ObjectDB and config, once " +
                "per world load. Machine-local and never synced, so turning it on for a server " +
                "boot does not affect any client. Leave it false in normal use.");
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(EconomySelfTestModule), nameof(WorldReady)));
        }

        private static void WorldReady()
        {
            if (_ran || _selfTest == null || !_selfTest.Value) return;
            _ran = true;
            Run();
        }

        internal static void Run()
        {
            Log.LogInfo("[EconomySelfTest] --- begin ---");
            One("TrailingTierDiscount", TrailingTierDiscountModule.SelfTest);
            One("RichSmelting", RichSmeltingModule.SelfTest);
            One("TraderStock", TraderStockModule.SelfTest);
            Log.LogInfo("[EconomySelfTest] --- end ---");
        }

        private static void One(string name, Func<string> f)
        {
            try
            {
                foreach (var line in f().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[EconomySelfTest] " + name + " threw: " + e);
            }
        }

        public override string StatusDetail()
        {
            return "SelfTest=" + (_selfTest != null && _selfTest.Value) + (_ran ? " (already run)" : "");
        }
    }
}
