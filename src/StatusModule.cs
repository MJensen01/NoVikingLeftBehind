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

                new Terminal.ConsoleCommand("nvlb.catalog",
                    "nvlb.catalog [text] - every NoVikingLeftBehind setting with its hint, type, " +
                    "range, permission tier and whether it applies live. Optionally filtered.",
                    new Terminal.ConsoleEvent(RunCatalog));

                Log.LogInfo("[Status] console commands 'nvlb.status', 'nvlb.catalog' registered");
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

        /// <summary>
        /// `nvlb.catalog [text]` - dumps the ConfigCatalog, i.e. the metadata every setting
        /// declared at bind time. Same data the settings tab and the tweak door read, so if a
        /// row looks wrong in the menu this is where to check it. A dedicated server writes the
        /// same content to nvlb-catalog.tsv next to its cfg at boot, since it has no terminal.
        /// </summary>
        private static void RunCatalog(Terminal.ConsoleEventArgs args)
        {
            string filter = null;
            if (args != null && args.Args != null && args.Args.Length > 1)
                filter = string.Join(" ", args.Args, 1, args.Args.Length - 1);

            foreach (var line in ConfigCatalog.DumpLines(filter))
            {
                if (args != null && args.Context != null) args.Context.AddString(line);
                Log.LogInfo(line);
            }
        }
    }
}
