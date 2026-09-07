using System;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The `nvlb.status` console command. Client-side only: Terminal's command table lives on
    /// the client. On a dedicated server the same lines are written to the log at ZNet.Start by
    /// the plugin's bootstrap postfix, so the information is available on both halves.
    ///
    /// Registration hook: postfix on the private static Terminal.InitTerminal(), which is guarded
    /// by m_terminalInitialized and never clears the static `commands` dictionary, so adding ours
    /// afterwards is safe and happens exactly once.
    /// Signature verified in the 0.221.12 decompile (Terminal.decompiled.cs:146):
    ///   ConsoleCommand(string command, string description, ConsoleEvent action,
    ///                  bool isCheat = false, bool isNetwork = false, bool onlyServer = false,
    ///                  bool isSecret = false, bool allowInDevBuild = false, ...)
    /// </summary>
    internal sealed class StatusModule : FeatureModule
    {
        public override string Name => "Status";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Status";

        private static bool _registered;

        protected override void Bind()
        {
            // No settings of its own.
        }

        protected override void ApplyPatches()
        {
            var init = AccessTools.Method(typeof(Terminal), "InitTerminal");
            if (init == null)
                throw new Exception("Terminal.InitTerminal() not found");

            Harmony.Patch(init, postfix: new HarmonyMethod(typeof(StatusModule), nameof(RegisterCommand)));
        }

        private static void RegisterCommand()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.status",
                    "NoVikingLeftBehind: world tier, catch-up settings and module state",
                    new Terminal.ConsoleEvent(Run));
                Log.LogInfo("[Status] console command 'nvlb.status' registered");
            }
            catch (Exception e)
            {
                _registered = false;
                Log.LogError("[Status] could not register nvlb.status: " + e);
            }
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            foreach (var line in NoVikingLeftBehindPlugin.StatusLines())
            {
                if (args.Context != null) args.Context.AddString(line);
                Log.LogInfo(line);
            }
        }
    }
}
