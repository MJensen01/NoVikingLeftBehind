using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>Who may change a server-synced setting from the in-game settings tab.</summary>
    public enum TweakAccess
    {
        /// <summary>Any player on the server, except settings marked admin-only.</summary>
        Everyone,
        /// <summary>Only players listed in adminlist.txt.</summary>
        Admins
    }

    /// <summary>How a change is told to the rest of the server.</summary>
    public enum Announce
    {
        /// <summary>One chat line, attributed to the player who made the change.</summary>
        Chat,
        /// <summary>A top-left message on everyone's screen.</summary>
        Message,
        /// <summary>Both.</summary>
        Both,
        /// <summary>Nothing (the server log still records it).</summary>
        Off
    }

    /// <summary>
    /// **Access** - the permission and audit settings for the in-game settings tab, and the place
    /// the tweak door's RPCs get registered.
    ///
    /// The design is social rather than locked down (Matt's call): by default *everyone* on the
    /// server can change most settings, every change is announced with the changer's name and
    /// kept in a 20-deep undo history, and only the handful of settings that change the world's
    /// rules ([ServerKeys]), the mod's safety switches ([General] EnforceClientMod, [Debug]
    /// AllowTestCommands, every SelfTest / DryRun) and these permission settings themselves are
    /// admin-only. Setting <c>TweakAccess = Admins</c> flips the whole thing back to admins in one
    /// live change.
    ///
    /// RPC registration hooks <c>Game.Start</c>, which is exactly where vanilla registers its own
    /// routed RPCs and the first point where <c>ZRoutedRpc.instance</c> is guaranteed to exist.
    /// <c>ZRoutedRpc</c> is rebuilt on every <c>ZNet.Awake</c>, so the registration must run again
    /// on every world load - <see cref="TweakDoor.RegisterRpcs"/> keys its guard on the instance,
    /// not on a one-shot bool.
    /// </summary>
    internal sealed class AccessModule : FeatureModule
    {
        public override string Name => "Access";
        public override string Section => "Access";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Theme => "Server";
        public override string Hint => "Who may change settings from the in-game menu";

        protected override string EnabledDescription =>
            "Enable the in-game settings door: the RPC that lets players change settings from " +
            "Valheim's own Settings menu. Off = the tab still lists everything, but nothing can " +
            "be changed from it and only the cfg file (or an admin's ConfigurationManager) works.";

        protected override Opt EnabledOpt => base.EnabledOpt.Admin();

        private static AccessModule _inst;

        private ConfigEntry<TweakAccess> _who;
        private ConfigEntry<Announce> _announce;
        private ConfigEntry<int> _rate;
        private ConfigEntry<bool> _selfTest;

        private AccessModule() { _inst = this; }

        // ---- what the door asks -------------------------------------------------------------

        public static bool DoorOpen { get { return _inst != null && _inst.Active; } }

        public static bool AdminsOnly
        {
            get { return _inst != null && _inst._who != null && _inst._who.Value == TweakAccess.Admins; }
        }

        public static Announce AnnounceMode
        {
            get { return _inst != null && _inst._announce != null ? _inst._announce.Value : Announce.Chat; }
        }

        public static int RateLimit
        {
            get { return _inst != null && _inst._rate != null ? Math.Max(1, _inst._rate.Value) : 10; }
        }

        /// <summary>One line for the settings tab's header strip.</summary>
        public static string WhoMayTweakText()
        {
            if (!DoorOpen) return "Changing settings from this menu is switched off on this server.";
            return AdminsOnly
                ? "Only server admins can change settings here."
                : "Everyone on this server can change these settings.";
        }

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _inst = this;

            _who = BindSynced("TweakAccess", TweakAccess.Everyone,
                "Who may change a server-synced setting from the in-game settings tab. " +
                "Everyone = any player on the server (settings marked admin-only still are). " +
                "Admins = only players in adminlist.txt. Changing this takes effect immediately.",
                Opt.C("Who may change settings from the in-game menu").Admin());

            _announce = BindSynced("Announce", Announce.Chat,
                "How a change is told to the rest of the server. Chat = one chat line attributed " +
                "to the player who made it. Message = a top-left message on everyone's screen. " +
                "Both = both. Off = nothing on screen (the server log still records every change).",
                Opt.C("How changes are announced to everyone").Admin());

            _rate = BindSynced("MaxChangesPer10s", 10,
                "How many changes one player may make in any ten seconds before the server starts " +
                "refusing. Stops an accident (or a joke) from rewriting the whole config at once.",
                Opt.N("How many changes one player may make per ten seconds", 1, 60, 1).Admin());

            _selfTest = BindSynced("SelfTest", false,
                "Dedicated server only: at world load, drive the whole settings door once - change " +
                "a harmless setting through the same path a player's menu uses, prove the cfg file " +
                "on disk changed (with its timestamped backup), hold it for 45 seconds so it can be " +
                "seen from outside, then undo it. Everything is written to the log. Off by default.",
                Opt.B("Prove the settings door works, at server start").Admin());
        }

        /// <summary>Drives the self-test's delayed second half. Called from the plugin's Update.</summary>
        public static void Pump()
        {
            if (_inst == null || !_inst.Active) return;
            if (_inst._selfTest == null || !_inst._selfTest.Value) return;
            AccessSelfTest.Pump();
        }

        protected override void ApplyPatches()
        {
            var gameStart = AccessTools.Method(typeof(Game), "Start");
            if (gameStart == null)
                throw new Exception("Game.Start() not found - cannot register the settings RPCs");

            // Compile-time proof that the vanilla calls the door depends on still exist, so a
            // game update surfaces as FAILED(...) in the module summary rather than at runtime.
            if (AccessTools.Method(typeof(ZNet), "IsAdmin", new[] { typeof(string) }) == null)
                throw new Exception("ZNet.IsAdmin(string) not found");
            if (AccessTools.Method(typeof(ZNet), "GetPlayerList") == null)
                throw new Exception("ZNet.GetPlayerList() not found");

            Harmony.Patch(gameStart,
                postfix: new HarmonyMethod(typeof(AccessModule), nameof(GameStartPostfix)));
        }

        private static void GameStartPostfix()
        {
            try { TweakDoor.RegisterRpcs(); }
            catch (Exception e) { Log.LogError("[Access] RPC registration failed: " + e); }

            // The self-test drives the real door, so it can only run where the door is: on the
            // server, once the routed-RPC layer and the peer list exist.
            try
            {
                if (_inst != null && _inst.Active && ServerActive() &&
                    _inst._selfTest != null && _inst._selfTest.Value)
                    AccessSelfTest.Arm();
            }
            catch (Exception e) { Log.LogError("[Access] self-test failed to arm: " + e); }
        }

        public override string StatusDetail()
        {
            return "who=" + (_who != null ? _who.Value.ToString() : "?") +
                   " announce=" + (_announce != null ? _announce.Value.ToString() : "?") +
                   " rate=" + RateLimit + "/10s" +
                   " selfTest=" + (_selfTest != null && _selfTest.Value) + "  " + TweakDoor.StatusLine();
        }
    }
}
