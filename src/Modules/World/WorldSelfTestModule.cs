using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Headless proof for the two client-side World modules (PortalTrail, LongFires). Both run on
    /// a player's PC, so on a dedicated server they correctly report disabled(side) and never
    /// patch anything - that makes them unprovable by a server boot alone. Their DECISIONS are
    /// still pure functions of ObjectDB, ZNetScene, the frontier and the synced config, all of
    /// which the dedicated server has (same pattern as EconomySelfTestModule in §13-Economy).
    ///
    /// With [World] SelfTest = true (machine-local, default false, never synced) this module calls
    /// each module's internal SelfTest() once, after ZoneSystem.Start has run and the global keys
    /// are loaded, and writes the answers to the log. It patches nothing else and changes no game
    /// state.
    /// </summary>
    internal sealed class WorldSelfTestModule : FeatureModule
    {
        public override string Name => "WorldSelfTest";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "World";

        private static ConfigEntry<bool> _selfTest;
        private static bool _ran;

        protected override void Bind()
        {
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic. Log what PortalTrail and LongFires WOULD do with the current world " +
                "tier, ObjectDB and ZNetScene, once per world load. Machine-local and never " +
                "synced, so turning it on for a server boot does not affect any client. Leave it " +
                "false in normal use.");
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(WorldSelfTestModule), nameof(WorldReady)));
        }

        private static void WorldReady()
        {
            if (_ran || _selfTest == null || !_selfTest.Value) return;
            _ran = true;
            Run();
        }

        internal static void Run()
        {
            Log.LogInfo("[WorldSelfTest] --- begin ---");
            One("PortalTrail", PortalTrailModule.SelfTest);
            One("LongFires", LongFiresModule.SelfTest);
            Log.LogInfo("[WorldSelfTest] --- end ---");
        }

        private static void One(string name, Func<string> f)
        {
            try
            {
                foreach (var line in f().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[WorldSelfTest] " + name + " threw: " + e);
            }
        }

        public override string StatusDetail()
        {
            return "SelfTest=" + (_selfTest != null && _selfTest.Value) + (_ran ? " (already run)" : "");
        }
    }
}
