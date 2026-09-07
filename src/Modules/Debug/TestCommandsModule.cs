using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **TestCommands** - a small admin-only test helper: `nvlb.give`, `nvlb.power`, `nvlb.tier`.
    ///
    /// Why it exists: on a DEDICATED server the vanilla console refuses every cheat command for a
    /// connected client. `Terminal.IsCheatsEnabled()` requires `ZNet.instance.IsServer()`, which is
    /// false for everyone but the host of a local game, so `spawn` / `setpower` cannot be used to
    /// test client-side features (DualPowers, ExtraSlots, CorpseRunPlus...) on a private test
    /// server. These three commands are registered with `isCheat: false` and do their work purely
    /// on the LOCAL player - inventory and guardian powers are client-side state, so no RPC and no
    /// server permission is involved.
    ///
    /// The gate is a SERVER-synced bool, `[Debug] AllowTestCommands` (BindSynced, default **false**).
    /// The commands always exist on the client, but they refuse to do anything and print
    /// "disabled by server" unless the server the client is connected to has turned the flag on.
    /// On NEWWORLD it stays false, so nobody can hand themselves items.
    ///
    /// Registration hook: the same postfix on the private static `Terminal.InitTerminal()` that
    /// StatusModule uses - it is guarded by `m_terminalInitialized` and never clears the static
    /// `commands` dictionary, so adding ours afterwards is safe and happens exactly once.
    /// Constructor signature verified in the 0.221.13 decompile (Terminal.cs:146):
    ///   ConsoleCommand(string command, string description, ConsoleEvent action,
    ///                  bool isCheat = false, bool isNetwork = false, bool onlyServer = false,
    ///                  bool isSecret = false, bool allowInDevBuild = false,
    ///                  ConsoleOptionsFetcher optionsFetcher = null, ...)
    /// `ConsoleOptionsFetcher` is `delegate List&lt;string&gt; ConsoleOptionsFetcher()` (Terminal.cs:256),
    /// so tab-completion is a parameterless list provider - used here for item and power names.
    /// </summary>
    internal sealed class TestCommandsModule : FeatureModule
    {
        public override string Name => "TestCommands";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Debug";

        private static TestCommandsModule _inst;
        private static bool _registered;

        private ConfigEntry<bool> _allow;

        private TestCommandsModule() { _inst = this; }

        /// <summary>Server-synced master switch. False (the default) = every command refuses.</summary>
        public static bool Allowed
        {
            get { return _inst != null && _inst.Active && _inst._allow != null && _inst._allow.Value; }
        }

        protected override void Bind()
        {
            _inst = this;
            _allow = BindSynced("AllowTestCommands", false,
                "SERVER setting: let connected clients use the nvlb test commands " +
                "(nvlb.give, nvlb.power, nvlb.tier) on their own character. These only touch the " +
                "local player's inventory and guardian powers - no world edits, no RPC - but they " +
                "are still cheats, so this is OFF by default and only the server can turn it on. " +
                "Intended for a private test server.");
        }

        protected override void ApplyPatches()
        {
            var init = AccessTools.Method(typeof(Terminal), "InitTerminal");
            if (init == null)
                throw new Exception("Terminal.InitTerminal() not found");

            if (AccessTools.Method(typeof(Inventory), "AddItem",
                    new[]
                    {
                        typeof(string), typeof(int), typeof(int), typeof(int),
                        typeof(long), typeof(string), typeof(bool)
                    }) == null)
                throw new Exception("Inventory.AddItem(string,int,int,int,long,string,bool) not found");

            if (AccessTools.Method(typeof(Player), "SetGuardianPower", new[] { typeof(string) }) == null)
                throw new Exception("Player.SetGuardianPower(string) not found");

            Harmony.Patch(init, postfix: new HarmonyMethod(typeof(TestCommandsModule), nameof(RegisterCommands)));
        }

        public override string StatusDetail()
        {
            return "testCommands=" + (Allowed ? "on" : "off") +
                   " (AllowTestCommands=" + (_allow != null && _allow.Value) + ")";
        }

        // ---- registration ------------------------------------------------------------------

        private static void RegisterCommands()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.give",
                    "nvlb.give <ItemPrefab> [amount] [quality] - add an item to your own inventory " +
                    "(needs [Debug] AllowTestCommands on the server)",
                    new Terminal.ConsoleEvent(CmdGive), false, false, false, false, false,
                    new Terminal.ConsoleOptionsFetcher(ItemOptions));

                new Terminal.ConsoleCommand("nvlb.power",
                    "nvlb.power <list|clear 1|2|swap|GP_Name [1|2]> - list guardian powers, empty a " +
                    "slot, exchange slot 1 and 2, or (with [Debug] AllowTestCommands) put a power " +
                    "in a slot. 'clear' and 'swap' are ordinary player actions and are never gated.",
                    new Terminal.ConsoleEvent(CmdPower), false, false, false, false, false,
                    new Terminal.ConsoleOptionsFetcher(PowerOptions));

                new Terminal.ConsoleCommand("nvlb.tier",
                    "nvlb.tier - show the current frontier tier (changing it is a server setting)",
                    new Terminal.ConsoleEvent(CmdTier));

                Log.LogInfo("[TestCommands] console commands 'nvlb.give', 'nvlb.power', 'nvlb.tier' registered");
            }
            catch (Exception e)
            {
                _registered = false;
                Log.LogError("[TestCommands] could not register commands: " + e);
            }
        }

        // ---- shared helpers ----------------------------------------------------------------

        private static void Print(Terminal.ConsoleEventArgs args, string line)
        {
            if (args != null && args.Context != null) args.Context.AddString(line);
            Log.LogInfo("[TestCommands] " + line);
        }

        /// <summary>Common gate: module on, server flag on, and a local player to act upon.</summary>
        private static bool Gate(Terminal.ConsoleEventArgs args, out Player player)
        {
            player = null;
            if (!Allowed)
            {
                Print(args, "nvlb test commands are disabled by server " +
                            "([Debug] AllowTestCommands = false).");
                return false;
            }
            player = Player.m_localPlayer;
            if (player == null)
            {
                Print(args, "no local player - join a world first.");
                return false;
            }
            return true;
        }

        private static List<string> ItemNames()
        {
            var names = new List<string>();
            var db = ObjectDB.instance;
            if (db == null || db.m_items == null) return names;
            foreach (var go in db.m_items)
            {
                if (go == null) continue;
                if (go.GetComponent<ItemDrop>() == null) continue;
                names.Add(go.name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static List<string> PowerNames()
        {
            var names = new List<string>();
            var db = ObjectDB.instance;
            if (db == null || db.m_StatusEffects == null) return names;
            foreach (var se in db.m_StatusEffects)
            {
                if (se == null || string.IsNullOrEmpty(se.name)) continue;
                if (se.name.StartsWith("GP_", StringComparison.OrdinalIgnoreCase)) names.Add(se.name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static List<string> ItemOptions()
        {
            try { return ItemNames(); }
            catch (Exception e) { Log.LogWarning("[TestCommands] item options: " + e.Message); return new List<string>(); }
        }

        private static List<string> PowerOptions()
        {
            try
            {
                var l = PowerNames();
                l.Insert(0, "swap");
                l.Insert(0, "clear");
                l.Insert(0, "list");
                return l;
            }
            catch (Exception e) { Log.LogWarning("[TestCommands] power options: " + e.Message); return new List<string>(); }
        }

        /// <summary>Exact match first, then a case-insensitive one; null when nothing matches.</summary>
        private static string ResolveName(List<string> pool, string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return null;
            foreach (var n in pool) if (n == wanted) return n;
            foreach (var n in pool) if (string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        /// <summary>"did you mean" - substring search over the pool, at most 8 suggestions.</summary>
        private static string DidYouMean(List<string> pool, string wanted)
        {
            var hits = new List<string>();
            if (!string.IsNullOrEmpty(wanted))
            {
                foreach (var n in pool)
                {
                    if (n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hits.Add(n);
                    if (hits.Count >= 8) break;
                }
            }
            if (hits.Count == 0) return "";
            return "  did you mean: " + string.Join(", ", hits.ToArray());
        }

        // ---- nvlb.give ---------------------------------------------------------------------

        private static void CmdGive(Terminal.ConsoleEventArgs args)
        {
            try
            {
                Player player;
                if (!Gate(args, out player)) return;

                if (args.Length < 2)
                {
                    Print(args, "usage: nvlb.give <ItemPrefab> [amount] [quality]   e.g. nvlb.give TrophyTheElder");
                    return;
                }

                var db = ObjectDB.instance;
                if (db == null) { Print(args, "ObjectDB is not ready yet."); return; }

                string wanted = args[1];
                var pool = ItemNames();
                string name = ResolveName(pool, wanted);
                if (name == null)
                {
                    Print(args, "unknown item prefab '" + wanted + "'." + DidYouMean(pool, wanted));
                    return;
                }

                int amount = args.Length > 2 ? args.TryParameterInt(2, 1) : 1;
                if (amount < 1) amount = 1;
                int quality = args.Length > 3 ? args.TryParameterInt(3, 1) : 1;
                if (quality < 1) quality = 1;

                var prefab = db.GetItemPrefab(name);
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null) { Print(args, "'" + name + "' is not an item."); return; }

                int maxQuality = drop.m_itemData.m_shared.m_maxQuality;
                if (quality > maxQuality)
                {
                    Print(args, "quality " + quality + " clamped to max " + maxQuality + " for " + name + ".");
                    quality = maxQuality;
                }

                var inv = player.GetInventory();
                if (inv == null) { Print(args, "no inventory."); return; }

                int added = 0;
                for (int i = 0; i < amount; i++)
                {
                    var item = inv.AddItem(name, 1, quality, 0, player.GetPlayerID(), player.GetPlayerName());
                    if (item == null) break;
                    added++;
                }

                if (added == 0) Print(args, "could not add " + name + " - inventory full?");
                else Print(args, "added " + added + "x " + name +
                                 (quality > 1 ? " (quality " + quality + ")" : "") +
                                 (added < amount ? "  [" + (amount - added) + " did not fit]" : ""));
            }
            catch (Exception e)
            {
                Print(args, "nvlb.give failed: " + e.Message);
                Log.LogError("[TestCommands] nvlb.give: " + e);
            }
        }

        // ---- nvlb.power --------------------------------------------------------------------

        private static void CmdPower(Terminal.ConsoleEventArgs args)
        {
            try
            {
                // `clear` and `swap` only move powers the player already earned between the slots
                // the mod gave them - they hand out nothing - so they are NOT behind
                // [Debug] AllowTestCommands. They are the console half of the same UX fix as the
                // altar messages: a way out of "the wrong power is in the wrong slot".
                if (args.Length > 1 && string.Equals(args[1], "clear", StringComparison.OrdinalIgnoreCase))
                {
                    CmdPowerClear(args);
                    return;
                }
                if (args.Length > 1 && string.Equals(args[1], "swap", StringComparison.OrdinalIgnoreCase))
                {
                    CmdPowerSwap(args);
                    return;
                }

                Player player;
                if (!Gate(args, out player)) return;

                var pool = PowerNames();

                if (args.Length < 2 || string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
                {
                    Print(args, "guardian powers in ObjectDB (" + pool.Count + "):");
                    foreach (var n in pool)
                    {
                        int slot = PowerSlots.FindSlot(player, n);
                        Print(args, "  " + n + (slot >= 0 ? "   <- slot " + (slot + 1) : ""));
                    }
                    Print(args, "current: " + PowerSlots.Describe(player));
                    Print(args, "usage: nvlb.power <GP_Name> [1|2]   e.g. nvlb.power GP_TheElder 2");
                    Print(args, "       nvlb.power clear 1|2   |   nvlb.power swap   (always allowed)");
                    return;
                }

                string name = ResolveName(pool, args[1]);
                if (name == null)
                {
                    Print(args, "unknown guardian power '" + args[1] + "'." + DidYouMean(pool, args[1]));
                    return;
                }

                int slotNo = args.Length > 2 ? args.TryParameterInt(2, 1) : 1;
                if (slotNo < 1 || slotNo > PowerSlots.MaxSlots)
                {
                    Print(args, "slot must be 1.." + PowerSlots.MaxSlots + ".");
                    return;
                }

                if (slotNo == 1)
                {
                    player.SetGuardianPower(name);
                    Print(args, "slot 1 (vanilla) = " + name);
                }
                else
                {
                    if (!DualPowersModule.IsActive)
                    {
                        Print(args, "slot " + slotNo + " needs the DualPowers module, which is off. " +
                                    "Turn on [Powers] Enabled (server setting) or use slot 1.");
                        return;
                    }
                    if (slotNo - 1 > PowerSlots.ExtraCount)
                    {
                        Print(args, "slot " + slotNo + " is not configured - [Powers] Slots = " +
                                    PowerSlots.SlotCount + ".");
                        return;
                    }
                    PowerSlots.SetPower(player, slotNo - 1, name);
                    Print(args, "slot " + slotNo + " (DualPowers) = " + name);
                }

                Print(args, "now: " + PowerSlots.Describe(player));
            }
            catch (Exception e)
            {
                Print(args, "nvlb.power failed: " + e.Message);
                Log.LogError("[TestCommands] nvlb.power: " + e);
            }
        }

        /// <summary>
        /// `nvlb.power clear 1|2` - empty one power slot. Ungated: it takes a power away, it never
        /// gives one, so it is a legitimate player action even on a locked-down server.
        /// </summary>
        private static void CmdPowerClear(Terminal.ConsoleEventArgs args)
        {
            var me = Player.m_localPlayer;
            if (me == null) { Print(args, "no local player - join a world first."); return; }

            if (args.Length < 3)
            {
                Print(args, "usage: nvlb.power clear <1|2>   (slot 1 is the " +
                            DualPowersModule.KeyLabel(0) + " power, slot 2 the " +
                            DualPowersModule.KeyLabel(1) + " power)");
                return;
            }
            int slotNo = args.TryParameterInt(2, 0);
            if (slotNo < 1 || slotNo > PowerSlots.MaxSlots)
            {
                Print(args, "slot must be 1.." + PowerSlots.MaxSlots + ".");
                return;
            }
            if (slotNo > 1 && !DualPowersModule.IsActive)
            {
                Print(args, "slot " + slotNo + " needs the DualPowers module, which is off.");
                return;
            }

            string had = DualPowersModule.ClearSlot(me, slotNo - 1);
            Print(args, string.IsNullOrEmpty(had)
                ? "slot " + slotNo + " was already empty."
                : "slot " + slotNo + " cleared (was " + had + ").");
            Print(args, "now: " + PowerSlots.Describe(me));
        }

        /// <summary>`nvlb.power swap` - exchange slot 1 and slot 2, cooldowns included. Ungated.</summary>
        private static void CmdPowerSwap(Terminal.ConsoleEventArgs args)
        {
            var me = Player.m_localPlayer;
            if (me == null) { Print(args, "no local player - join a world first."); return; }
            if (!DualPowersModule.IsActive || PowerSlots.ExtraCount < 1)
            {
                Print(args, "swapping needs the DualPowers module with at least 2 slots.");
                return;
            }

            DualPowersModule.SwapSlots(me);
            Print(args, "swapped slots 1 and 2.");
            Print(args, "now: " + PowerSlots.Describe(me));
        }

        // ---- nvlb.tier ---------------------------------------------------------------------

        private static void CmdTier(Terminal.ConsoleEventArgs args)
        {
            try
            {
                Player player;
                if (!Gate(args, out player)) return;

                Print(args, "WorldTier=" + Frontier.Describe() +
                            "  auto=" + Frontier.AutoTier +
                            " (key " + (Frontier.KeyFor(Frontier.AutoTier) ?? "none") + ")" +
                            "  TiersBehind=" + (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1) +
                            "  behind-the-frontier tiers: " + Frontier.BehindRangeText());

                if (args.Length > 1)
                {
                    Print(args, "the tier is a SERVER setting - a client cannot change it.");
                    Print(args, "on the server run:  cfg.py set nvlb Frontier.TierOverride " +
                                (string.Equals(args[1], "auto", StringComparison.OrdinalIgnoreCase) ? "-1" : args[1]) +
                                "   (-1 = auto, follow the boss keys)");
                }
            }
            catch (Exception e)
            {
                Print(args, "nvlb.tier failed: " + e.Message);
                Log.LogError("[TestCommands] nvlb.tier: " + e);
            }
        }
    }
}
