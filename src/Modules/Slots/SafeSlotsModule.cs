using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **SafeSlots** - nobody should have to think about backing up their extra-slot items.
    ///
    /// ExtraSlots already keeps those items out of the vanilla save package (SlotStore rule 1) and
    /// keeps three rolling copies of the blob inside the character's own custom data (rule 4). That
    /// covers everything a modded client can do to itself. Two holes are left, and this module
    /// closes both:
    ///
    ///   1. **A character launched in VANILLA single-player before its first modded login.** The
    ///      items are still parked at out-of-grid positions by whatever mod put them there
    ///      (shudnal's ExtraSlots), and vanilla's <c>Inventory.Load</c> deletes them without a
    ///      word - QOL-ARCHITECTURE section 4's opening bug. <see cref="SlotsRescue"/> catches them
    ///      when NVLB is present, but the .fch is rewritten on the next save, so there is exactly
    ///      one moment at which the original is still on disk: the load itself. So we copy the
    ///      <c>.fch</c> there and then - see <see cref="CharacterBackup"/> - and tell the player,
    ///      once, what moved (the migration receipt).
    ///
    ///   2. **A local <c>.fch</c> lost or corrupted.** After the first NVLB login the items live in
    ///      the player's custom data, which vanilla preserves - but only inside that one file. So
    ///      the client also uploads the blob to the SERVER after every character save, and the
    ///      server keeps five versions per character under <c>&lt;cfgdir&gt;/nvlb/vault/</c> - see
    ///      <see cref="SlotVault"/>. On a fresh install with an empty extra-slot store, the client
    ///      is TOLD the vault has items and how to take them back. It never restores by itself: a
    ///      deliberately emptied set of slots must stay empty.
    ///
    /// The server rejects clients without the mod ([General] EnforceClientMod), so those two are
    /// the whole exposure. Everything here is additive - turning [SafeSlots] Enabled off leaves
    /// ExtraSlots exactly as it was in 0.7.6.
    /// </summary>
    internal sealed class SafeSlotsModule : FeatureModule
    {
        public override string Name => "SafeSlots";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "SafeSlots";
        public override string Theme => "Inventory";
        public override string Hint => "Backs up your extra-slot items, locally and to the server";

        protected override string EnabledDescription =>
            "Keep a safety net under the extra-slot items: a copy of your character file before a " +
            "migration, and a copy of the items themselves on the server. Off = ExtraSlots behaves " +
            "exactly as it did before 0.8.0 (the items are still kept out of the vanilla save " +
            "package, with three rolling backups inside the character).";

        // ---- RPC names ---------------------------------------------------------------------------

        /// <summary>client -&gt; server: here is my extra-slot blob (playerId, playerName, blob).</summary>
        public const string RpcPut = "NVLB_VaultPut";
        /// <summary>client -&gt; server: what do you hold for me? (playerId, playerName).</summary>
        public const string RpcList = "NVLB_VaultList";
        /// <summary>client -&gt; server: send me version n (playerId, index).</summary>
        public const string RpcGet = "NVLB_VaultGet";
        /// <summary>server -&gt; client: the version list, one per line.</summary>
        public const string RpcInfo = "NVLB_VaultInfo";
        /// <summary>server -&gt; client: (ok, blob-or-message).</summary>
        public const string RpcData = "NVLB_VaultData";

        // ---- config -----------------------------------------------------------------------------

        internal static SafeSlotsModule Inst;

        private ConfigEntry<bool> _characterBackup;
        private ConfigEntry<int> _keepBackups;
        private ConfigEntry<int> _vaultVersions;
        private ConfigEntry<int> _minUploadSec;
        private ConfigEntry<bool> _selfTest;

        internal static bool SelfTestWanted
        {
            get { return Inst != null && Inst._selfTest != null && Inst._selfTest.Value; }
        }

        internal static int VaultVersions
        {
            get { return Inst == null || Inst._vaultVersions == null ? 5 : Mathf.Clamp(Inst._vaultVersions.Value, 1, 20); }
        }

        private static float MinUploadInterval
        {
            get { return Inst == null || Inst._minUploadSec == null ? 30f : Mathf.Max(0f, Inst._minUploadSec.Value); }
        }

        private static int KeepBackups
        {
            get { return Inst == null || Inst._keepBackups == null ? 3 : Mathf.Clamp(Inst._keepBackups.Value, 1, 20); }
        }

        private static bool BackupsOn
        {
            get
            {
                // ClientActive() as well as Active: SlotsSelfTest arms the same rescue hook on a
                // dedicated server, which has a PlayerProfile with no file behind it.
                return Inst != null && Inst.Active && ClientActive() &&
                       Inst._characterBackup != null && Inst._characterBackup.Value;
            }
        }

        protected override void Bind()
        {
            Inst = this;

            _characterBackup = BindLocal("CharacterBackup", true,
                "Local: before this mod moves anything for the first time, copy your character's " +
                ".fch file into characters_local\\" + CharacterBackup.FolderName + "\\. Taken on a " +
                "character's first login with this mod, and again any time items are rescued from " +
                "an older extra-slots mod. Never synced - it is your own machine's disk.",
                Opt.B("Copy your character file before this mod first changes it"));

            _keepBackups = BindLocal("KeepBackups", 3,
                "Local: how many character-file backups to keep per character. The oldest is " +
                "deleted once there are more than this.",
                Opt.N("How many character-file backups to keep", 1, 20, 1));

            _vaultVersions = BindSynced("VaultVersions", 5,
                "Server: how many versions of each character's extra-slot items the server vault " +
                "keeps, newest first. Stored one file per character under the server's " +
                "config/nvlb/vault/ directory.",
                Opt.N("How many versions the server vault keeps per character", 1, 20, 1));

            _minUploadSec = BindSynced("MinUploadIntervalSec", 30,
                "Server: the shortest gap between two vault uploads from the same client. A save " +
                "whose items have not changed is never uploaded at all, so this only limits how " +
                "often a player who is actively reshuffling their slots can write to the vault. " +
                "Logging out always uploads, whatever this says.",
                Opt.N("Shortest gap between vault uploads from one player", 0, 600, 5));

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic. Once per world load, prove the vault headlessly: write a blob, list " +
                "it, read it back byte for byte, enforce the version cap and the 64 KB size guard, " +
                "and check the login decision that offers a restore. Writes and then deletes one " +
                "throwaway vault file. Machine-local, never synced.",
                Opt.B("Prove the server vault works with no players online").Admin().Restart());
        }

        // ---- patches ------------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            Inst = this;
            var self = typeof(SafeSlotsModule);

            // Both halves: the routed RPCs, registered where vanilla registers its own and where
            // ZRoutedRpc.instance is first guaranteed to exist (same reasoning as AccessModule).
            var gameStart = AccessTools.Method(typeof(Game), "Start");
            if (gameStart == null) throw new Exception("Game.Start() not found - cannot register the vault RPCs");
            Harmony.Patch(gameStart, postfix: new HarmonyMethod(self, nameof(GameStartPostfix)));

            if (NoVikingLeftBehindPlugin.IsServerSide)
            {
                // A dedicated server has no PlayerProfile, no Terminal and never logs out of
                // itself, so none of the client hooks below exist for it. Nothing to patch.
                Log.LogInfo("[SafeSlots] server half: vault at " + SlotVault.Dir);
                return;
            }

            var savePlayerData = AccessTools.Method(typeof(PlayerProfile), "SavePlayerData",
                                                    new[] { typeof(Player) });
            var logout = AccessTools.Method(typeof(Game), "Logout", new[] { typeof(bool), typeof(bool) });
            var termInit = AccessTools.Method(typeof(Terminal), "InitTerminal");

            if (savePlayerData == null) throw new Exception("PlayerProfile.SavePlayerData(Player) not found");
            if (logout == null) throw new Exception("Game.Logout(bool,bool) not found");
            if (termInit == null) throw new Exception("Terminal.InitTerminal() not found");
            if (AccessTools.Method(typeof(PlayerProfile), "GetPlayerID") == null)
                throw new Exception("PlayerProfile.GetPlayerID() not found");
            if (AccessTools.Method(typeof(PlayerProfile), "GetPath", new Type[0]) == null)
                throw new Exception("PlayerProfile.GetPath() not found");
            if (AccessTools.Method(typeof(SaveSystem), "GetCharacterFolderPath",
                                   new[] { typeof(FileHelpers.FileSource) }) == null)
                throw new Exception("SaveSystem.GetCharacterFolderPath(FileSource) not found");

            Harmony.Patch(savePlayerData, postfix: new HarmonyMethod(self, nameof(SavePlayerDataPostfix)));
            Harmony.Patch(logout, prefix: new HarmonyMethod(self, nameof(LogoutPrefix)));
            Harmony.Patch(termInit, postfix: new HarmonyMethod(self, nameof(RegisterCommand)));
        }

        public override void Disable()
        {
            base.Disable();
            if (Inst == this) Inst = null;
        }

        private static bool ClientLive()
        {
            return Inst != null && Inst.Active && ClientActive();
        }

        // ---- registration + the server's boot line ---------------------------------------------------

        private static void GameStartPostfix()
        {
            try { RegisterRpcs(); }
            catch (Exception e) { Log.LogError("[SafeSlots] RPC registration failed: " + e); }

            try
            {
                if (Inst != null && Inst.Active && ServerActive())
                    Log.LogInfo("[SafeSlots] " + SlotVault.Summary() + "  (" + SlotVault.Dir + ")");
            }
            catch (Exception e) { Log.LogWarning("[SafeSlots] vault summary failed: " + e.Message); }

            try
            {
                if (SelfTestWanted) SafeSlotsSelfTest.Run();
            }
            catch (Exception e) { Log.LogError("[SafeSlots][SelfTest] threw: " + e); }
        }

        private static object _registeredOn;

        private static void RegisterRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            if (ReferenceEquals(_registeredOn, rpc)) return;
            _registeredOn = rpc;

            _owners.Clear();
            _lastSentHash = null;
            _lastUpload = -9999f;
            ForgetOffer();

            try
            {
                // Both directions on both halves: a listen-server host is client and server at once.
                rpc.Register<string, string, string>(RpcPut, RPC_Put);
                rpc.Register<string, string>(RpcList, RPC_List);
                rpc.Register<string, int>(RpcGet, RPC_Get);
                rpc.Register<string>(RpcInfo, RPC_Info);
                rpc.Register<int, string>(RpcData, RPC_Data);
                Log.LogInfo("[SafeSlots] registered RPCs " + RpcPut + " / " + RpcList + " / " +
                            RpcGet + " / " + RpcInfo + " / " + RpcData);
            }
            catch (Exception e)
            {
                _registeredOn = null;
                Log.LogError("[SafeSlots] could not register the vault RPCs: " + e);
            }
        }

        // ==============================================================================================
        // CLIENT
        // ==============================================================================================

        private static float _lastUpload = -9999f;
        private static string _lastSentHash;

        /// <summary>Set while a login is waiting for the server's answer, so ONE offer can be shown.</summary>
        private static bool _offerWanted;
        private static bool _listRequested;

        /// <summary>The newest version list the server sent, newest first. Drives the console command.</summary>
        private static readonly List<VaultVersion> _known = new List<VaultVersion>();

        private static void ForgetOffer()
        {
            _offerWanted = false;
            _listRequested = false;
            _known.Clear();
        }

        /// <summary>This character's stable id, or "" when there is no profile yet.</summary>
        internal static string LocalPlayerId()
        {
            try
            {
                var g = Game.instance;
                if (g == null) return "";
                var p = g.GetPlayerProfile();
                if (p == null) return "";
                return p.GetPlayerID().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        private static string LocalPlayerName()
        {
            try
            {
                var g = Game.instance;
                if (g == null) return "";
                var p = g.GetPlayerProfile();
                return p == null ? "" : (p.GetName() ?? "");
            }
            catch { return ""; }
        }

        // ---- hook 1: the character is about to be loaded --------------------------------------------

        /// <summary>
        /// Called from ExtraSlots' <c>Player.Load</c> PREFIX - the last instant at which the .fch on
        /// disk is still exactly what it was before this mod ever touched the character. Takes the
        /// first-login backup here; the migration backup is taken from
        /// <see cref="NoteRescueCapture"/> a few microseconds later, on the first item the rescue
        /// actually catches, and the two are de-duplicated by the stamp in the file name.
        /// </summary>
        internal static void OnCharacterLoading()
        {
            _backedUpThisLoad = false;
            ForgetOffer();
            if (!BackupsOn) return;
            try
            {
                var g = Game.instance;
                var profile = g == null ? null : g.GetPlayerProfile();
                if (profile == null) return;
                if (CharacterBackup.HasAny(profile.GetFilename())) return;   // not our first time

                if (CharacterBackup.Take(profile, KeepBackups, "first login with this mod") != null)
                    _backedUpThisLoad = true;
            }
            catch (Exception e) { Log.LogError("[SafeSlots] first-login backup failed: " + e.Message); }
        }

        private static bool _backedUpThisLoad;

        /// <summary>
        /// Called from <see cref="SlotsRescue"/> the first time it captures an out-of-grid item on
        /// this load - i.e. the moment we know this character IS a migration and its .fch is the
        /// only copy of those items in existence.
        /// </summary>
        internal static void NoteRescueCapture()
        {
            if (_backedUpThisLoad || !BackupsOn) return;
            _backedUpThisLoad = true;
            try
            {
                var g = Game.instance;
                var profile = g == null ? null : g.GetPlayerProfile();
                if (profile == null) return;
                CharacterBackup.Take(profile, KeepBackups, "items found in an old extra-slots layout");
            }
            catch (Exception e) { Log.LogError("[SafeSlots] migration backup failed: " + e.Message); }
        }

        // ---- hook 2: the character is loaded ----------------------------------------------------------

        /// <summary>
        /// The migration receipt. Called from ExtraSlots' <c>Player.Load</c> POSTFIX right after
        /// <see cref="SlotsRescue.Place"/>, and reads that call's own report - so the numbers on
        /// screen are the numbers in the log, by construction.
        /// </summary>
        internal static void ShowRescueReceipt(Player player)
        {
            var r = SlotsRescue.LastReport;
            if (r == null || r.Total <= 0 || player == null) return;
            try
            {
                player.Message(MessageHud.MessageType.Center,
                    "Moved " + r.Total + " item" + (r.Total == 1 ? "" : "s") + " from your old extra slots");
                Log.LogWarning("[SafeSlots] migration receipt: moved " + r.Total + " item(s) - " +
                               r.ToSlots + " into extra slots, " + r.ToBag + " into the bag, " +
                               r.Dropped + " onto the ground: " + r.Names);

                // A distinct, louder line for anything that could not be carried: those are the
                // only ones the player has to physically walk over and pick up.
                if (r.Dropped > 0 && !string.IsNullOrEmpty(r.DroppedNames))
                {
                    player.Message(MessageHud.MessageType.Center,
                        "No room for " + r.DroppedNames + " - dropped at your feet");
                    Log.LogWarning("[SafeSlots] migration receipt: DROPPED at the player's feet: " +
                                   r.DroppedNames);
                }
            }
            catch (Exception e) { Log.LogError("[SafeSlots] receipt failed: " + e.Message); }
        }

        /// <summary>
        /// **The login decision.** Called from ExtraSlots' <c>Player.Load</c> POSTFIX after the
        /// blob has been injected and the rescue has placed whatever it caught, so
        /// <paramref name="localCount"/> is the final truth about this character's extra slots.
        ///
        /// We always ask the server what it holds (that is also what registers this connection as
        /// the owner of this character id, which is what lets a later restore be refused for
        /// anyone else). We only OFFER when the local store came up completely empty AND the
        /// rescue found nothing - i.e. this character has no extra-slot items at all right now. A
        /// player who deliberately emptied their slots into a chest sees nothing, and nothing is
        /// ever restored without them typing the command.
        /// </summary>
        internal static void OnCharacterLoaded(Player player, Inventory inv)
        {
            if (!ClientLive() || player == null) return;
            try
            {
                int localCount = SlotStore.Collect(inv).Count;
                int rescued = SlotsRescue.LastReport == null ? 0 : SlotsRescue.LastReport.Total;
                _offerWanted = ShouldAsk(localCount, rescued);

                Log.LogInfo("[SafeSlots] login: " + localCount + " item(s) in the extra slots, " +
                            rescued + " rescued -> " + (_offerWanted ? "asking" : "not asking") +
                            " the server vault for a restore offer");

                RequestList();
            }
            catch (Exception e) { Log.LogError("[SafeSlots] login check failed: " + e); }
        }

        /// <summary>
        /// Half the decision, split out so the self test can drive it with no game around it:
        /// the client only wants an offer when it is holding nothing of its own.
        /// </summary>
        internal static bool ShouldAsk(int localCount, int rescuedCount)
        {
            return localCount <= 0 && rescuedCount <= 0;
        }

        /// <summary>The other half: the server has something worth offering.</summary>
        internal static bool ShouldOffer(int localCount, int rescuedCount, int vaultCount)
        {
            return ShouldAsk(localCount, rescuedCount) && vaultCount > 0;
        }

        private static void RequestList()
        {
            var id = LocalPlayerId();
            if (string.IsNullOrEmpty(id) || id == "0") return;
            if (ZRoutedRpc.instance == null) return;
            _listRequested = true;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcList, id, LocalPlayerName());
        }

        // ---- hook 3: the character was saved -----------------------------------------------------------

        private static void SavePlayerDataPostfix(PlayerProfile __instance, Player player)
        {
            if (!ClientLive() || player == null || player != Player.m_localPlayer) return;
            try
            {
                // The save that runs on the way out (Game.Shutdown -> SavePlayerProfile, which is
                // also the alt-F4 path through OnApplicationQuit) is the one upload that must not
                // be dropped by the throttle - there will not be another.
                var g = Game.instance;
                Upload(__instance, player, g != null && g.IsShuttingDown());
            }
            catch (Exception e) { Log.LogError("[SafeSlots] vault upload failed: " + e); }
        }

        private static void LogoutPrefix()
        {
            if (!ClientLive()) return;
            try
            {
                var g = Game.instance;
                var p = Player.m_localPlayer;
                if (g == null || p == null) return;
                Upload(g.GetPlayerProfile(), p, true);
            }
            catch (Exception e) { Log.LogError("[SafeSlots] logout upload failed: " + e); }
            finally { ForgetOffer(); _lastSentHash = null; }
        }

        /// <summary>
        /// Send the blob to the server. Skipped when nothing changed since the last upload (the
        /// common case - most saves do not touch the extra slots) and, unless
        /// <paramref name="force"/>, when the last upload was less than MinUploadIntervalSec ago.
        /// </summary>
        private static void Upload(PlayerProfile profile, Player player, bool force)
        {
            if (profile == null || player == null) return;
            if (ZRoutedRpc.instance == null) return;

            var id = profile.GetPlayerID().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(id) || id == "0") return;

            var blob = CurrentBlob(player, force);
            if (blob == null) return;

            var hash = SlotVault.Hash(blob);
            if (hash == _lastSentHash) return;

            if (!force && Time.realtimeSinceStartup - _lastUpload < MinUploadInterval) return;

            _lastUpload = Time.realtimeSinceStartup;
            _lastSentHash = hash;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcPut, id, profile.GetName() ?? "", blob);
            Log.LogInfo("[SafeSlots] uploaded " + SlotVault.CountIn(blob) + " extra-slot item(s) to " +
                        "the server vault (" + blob.Length + " chars, hash " + hash + ")" +
                        (force ? " on logout" : ""));
        }

        /// <summary>
        /// The blob to upload.
        ///
        /// On an ordinary save it is read straight out of custom data: <c>Player.Save</c>'s prefix
        /// has already written it there by the time this runs, so the vault stores byte for byte
        /// what the .fch stores. On a FORCED upload it is encoded from the live grid instead,
        /// because <c>Game.Logout</c> runs its prefix BEFORE <c>Shutdown -&gt; SavePlayerProfile</c>
        /// (Game.cs:321-347) - at that instant custom data still holds the PREVIOUS save, and the
        /// last thing a player did before logging out is exactly what must not be lost.
        /// </summary>
        private static string CurrentBlob(Player player, bool fresh)
        {
            try
            {
                var inv = player.GetInventory();
                bool managed = inv != null && ReferenceEquals(inv, SlotStore.Managed);

                // NEVER encode the grid while the save lift holds the items out of it: Collect
                // would see an empty extra area and we would upload - and, worse, teach the client
                // to believe - that this character owns nothing. Reading custom data is always safe,
                // because the lift never touches it, so fall through to that instead.
                if (SlotStore.Lifted)
                {
                    Log.LogWarning("[SafeSlots] asked for the extra-slot blob while the save lift " +
                                   "was open - reading the saved blob instead of the empty grid");
                    fresh = false;
                }

                if (fresh && managed) return SlotBlob.Encode(SlotStore.Collect(inv));

                string blob;
                if (player.m_customData != null &&
                    player.m_customData.TryGetValue(SlotStore.BlobKey, out blob) &&
                    !string.IsNullOrEmpty(blob)) return blob;

                return managed ? SlotBlob.Encode(SlotStore.Collect(inv)) : null;
            }
            catch (Exception e)
            {
                Log.LogWarning("[SafeSlots] could not read the current blob: " + e.Message);
                return null;
            }
        }

        // ---- server -> client -----------------------------------------------------------------------------

        private static void RPC_Info(long sender, string payload)
        {
            try
            {
                _known.Clear();
                if (!string.IsNullOrEmpty(payload))
                {
                    var lines = payload.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        // "utc|hash|count"
                        var parts = lines[i].Split('|');
                        if (parts.Length < 3) continue;
                        int n;
                        int.TryParse(parts[2], out n);
                        _known.Add(new VaultVersion { utc = parts[0], hash = parts[1], count = n });
                    }
                }

                Log.LogInfo("[SafeSlots] the server vault holds " + _known.Count +
                            " version(s) for this character");

                if (!_offerWanted || _known.Count == 0) { _offerWanted = false; return; }
                _offerWanted = false;

                VaultVersion best = null;
                for (int i = 0; i < _known.Count; i++)
                    if (_known[i].count > 0) { best = _known[i]; break; }
                if (best == null) return;

                var msg = "Your server vault holds " + best.count + " extra-slot item" +
                          (best.count == 1 ? "" : "s") + " (saved " + SlotVault.Ago(best.WhenUtc) +
                          "). Type nvlb.slots.vault restore to get them back.";
                var p = Player.m_localPlayer;
                if (p != null) p.Message(MessageHud.MessageType.TopLeft, msg);
                Log.LogWarning("[SafeSlots] " + msg);
            }
            catch (Exception e) { Log.LogError("[SafeSlots] vault info failed: " + e); }
        }

        private static void RPC_Data(long sender, int ok, string payload)
        {
            try
            {
                if (ok == 0) { Tell(payload); return; }
                RestoreFromBlob(payload);
            }
            catch (Exception e) { Log.LogError("[SafeSlots] vault restore failed: " + e); }
        }

        /// <summary>
        /// Put the vault's items back with EXACTLY the code path <c>nvlb.slots.restore</c> uses:
        /// <see cref="SlotStore.Inject"/> into the slot each item was keyed to (or any other slot
        /// that fits), then <see cref="SlotStore.Evacuate"/> for the leftovers - which tries the
        /// bag before it ever considers the ground. Items that are already sitting where they
        /// belong are recognised first and left alone, so restoring twice is a no-op rather than a
        /// pile of duplicates.
        /// </summary>
        private static void RestoreFromBlob(string blob)
        {
            var p = Player.m_localPlayer;
            if (p == null) { Tell("no local player"); return; }

            var entries = SlotBlob.Decode(blob);
            if (entries == null || entries.Count == 0)
            {
                Tell("that vault version is empty or unreadable");
                return;
            }

            var inv = p.GetInventory();
            var todo = new List<SlotEntry>();
            int already = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (AlreadyThere(inv, entries[i])) { already++; continue; }
                todo.Add(entries[i]);
            }

            if (todo.Count == 0)
            {
                Tell("all " + entries.Count + " item(s) from the vault are already present, nothing to do");
                return;
            }

            var leftovers = new List<ItemDrop.ItemData>();
            int placed = SlotStore.Inject(inv, todo, leftovers);
            SlotStore.Evacuate(p, inv, leftovers, "nvlb.slots.vault restore");
            SlotStore.Changed(inv);
            SlotsUi.Invalidate();

            Tell("restored " + placed + "/" + todo.Count + " item(s) from the server vault" +
                 (already > 0 ? " (" + already + " were already present)" : ""));
            if (p != null)
                p.Message(MessageHud.MessageType.Center,
                          "Restored " + placed + " item" + (placed == 1 ? "" : "s") + " from the server vault");
        }

        /// <summary>
        /// Is this exact entry already in its slot? Compared by prefab, stack and quality - the
        /// three things a player can see - rather than by identity, because a decoded blob item is
        /// always a fresh object.
        /// </summary>
        private static bool AlreadyThere(Inventory inv, SlotEntry e)
        {
            if (inv == null || e == null || e.Item == null) return false;
            var slot = SlotLayout.ByKey(e.SlotKey) ?? SlotLayout.LegacyKey(e.SlotKey);
            if (slot == null) return false;
            var at = inv.GetItemAt(slot.Pos.x, slot.Pos.y);
            if (at == null) return false;
            return SlotBlob.PrefabNameOf(at) == SlotBlob.PrefabNameOf(e.Item) &&
                   at.m_stack == e.Item.m_stack &&
                   at.m_quality == e.Item.m_quality;
        }

        // ==============================================================================================
        // SERVER
        // ==============================================================================================

        /// <summary>
        /// Which character each connected peer says it is playing. Filled by the FIRST message a
        /// client sends (the login list request, or an upload), and it is what makes a restore
        /// refusable: <see cref="RPC_Get"/> only ever hands back the vault of the character this
        /// connection is actually playing.
        /// </summary>
        private static readonly Dictionary<long, string> _owners = new Dictionary<long, string>();

        private static bool ServerHere()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        private static bool ServerLive()
        {
            return Inst != null && Inst.Active && ServerHere();
        }

        private static void RPC_Put(long sender, string playerId, string playerName, string blob)
        {
            if (!ServerLive()) return;
            try
            {
                if (string.IsNullOrEmpty(playerId) || playerId == "0") return;
                _owners[sender] = playerId;

                VaultFile vf;
                var result = SlotVault.Put(playerId, playerName, blob, VaultVersions, out vf);
                switch (result)
                {
                    case SlotVault.PutResult.Stored:
                        Log.LogInfo("[SafeSlots] vault: stored " + SlotVault.CountIn(blob) + " item(s) for " +
                                    SlotVault.Describe(playerId, playerName) + " (" +
                                    (vf == null ? 0 : vf.versions.Count) + "/" + VaultVersions + " versions kept)");
                        break;
                    case SlotVault.PutResult.Unchanged:
                        break;   // the common case; saying so every save would be noise
                    case SlotVault.PutResult.TooBig:
                        Reply(sender, false, "Your extra-slot items are too large for this server's vault " +
                                             "(over " + (SlotVault.MaxBlobBytes / 1024) + " KB) - they are " +
                                             "still saved in your own character file.");
                        break;
                    default:
                        Log.LogWarning("[SafeSlots] vault: refused a put from " +
                                       SlotVault.Describe(playerId, playerName) + " (" + result + ")");
                        break;
                }
            }
            catch (Exception e) { Log.LogError("[SafeSlots] vault put failed: " + e); }
        }

        private static void RPC_List(long sender, string playerId, string playerName)
        {
            if (!ServerLive()) return;
            try
            {
                if (string.IsNullOrEmpty(playerId) || playerId == "0") return;
                _owners[sender] = playerId;

                var vf = SlotVault.Read(playerId);
                var sb = new StringBuilder();
                for (int i = 0; i < vf.versions.Count; i++)
                {
                    var v = vf.versions[i];
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(v.utc).Append('|').Append(v.hash).Append('|').Append(v.count);
                }
                SendInfo(sender, sb.ToString());
            }
            catch (Exception e) { Log.LogError("[SafeSlots] vault list failed: " + e); }
        }

        private static void RPC_Get(long sender, string playerId, int index)
        {
            if (!ServerLive()) return;
            try
            {
                string owner;
                if (!_owners.TryGetValue(sender, out owner) || owner != playerId || string.IsNullOrEmpty(playerId))
                {
                    Log.LogWarning("[SafeSlots] vault: REFUSED a restore of " + playerId +
                                   " to a connection playing " + (owner ?? "an unknown character"));
                    Reply(sender, false, "That vault belongs to a different character.");
                    return;
                }

                var vf = SlotVault.Read(playerId);
                if (index < 0 || index >= vf.versions.Count)
                {
                    Reply(sender, false, vf.versions.Count == 0
                        ? "The server vault has nothing stored for this character yet."
                        : "The server vault only has " + vf.versions.Count + " version(s) for this character.");
                    return;
                }

                var v = vf.versions[index];
                Log.LogInfo("[SafeSlots] vault: sending version " + (index + 1) + "/" + vf.versions.Count +
                            " (" + v.count + " item(s), saved " + v.utc + ") to " +
                            SlotVault.Describe(playerId, vf.name));
                Reply(sender, true, v.blob);
            }
            catch (Exception e)
            {
                Log.LogError("[SafeSlots] vault get failed: " + e);
                Reply(sender, false, "The server could not read that vault version.");
            }
        }

        private static void SendInfo(long target, string payload)
        {
            if (ZNet.instance != null && target == ZNet.GetUID()) { RPC_Info(target, payload); return; }
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcInfo, payload);
        }

        private static void Reply(long target, bool ok, string payload)
        {
            if (ZNet.instance != null && target == ZNet.GetUID()) { RPC_Data(target, ok ? 1 : 0, payload); return; }
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcData, ok ? 1 : 0, payload);
        }

        // ==============================================================================================
        // console command
        // ==============================================================================================

        private static bool _commandRegistered;
        private static Terminal.ConsoleEventArgs _lastArgs;

        private static void RegisterCommand()
        {
            if (_commandRegistered) return;
            _commandRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.slots.vault",
                    "Server-side backup of your extra-slot items: 'nvlb.slots.vault' lists what the " +
                    "server holds, 'nvlb.slots.vault restore [n]' puts version n back (1 = newest)",
                    new Terminal.ConsoleEvent(VaultCommand));
                Log.LogInfo("[SafeSlots] console command 'nvlb.slots.vault' registered");
            }
            catch (Exception e)
            {
                _commandRegistered = false;
                Log.LogError("[SafeSlots] could not register nvlb.slots.vault: " + e);
            }
        }

        private static void VaultCommand(Terminal.ConsoleEventArgs args)
        {
            _lastArgs = args;
            if (!ClientLive()) { Tell("the SafeSlots module is off on this server"); return; }

            var id = LocalPlayerId();
            if (string.IsNullOrEmpty(id) || id == "0") { Tell("no character loaded"); return; }
            if (ZRoutedRpc.instance == null) { Tell("not connected to a server"); return; }

            bool restore = args.Length > 1 &&
                           string.Equals(args[1], "restore", StringComparison.OrdinalIgnoreCase);

            if (!restore)
            {
                if (_listRequested && _known.Count > 0)
                {
                    Tell("the server holds " + _known.Count + " version(s) for this character:");
                    for (int i = 0; i < _known.Count; i++)
                        Tell("  " + (i + 1) + ")  " + _known[i].count + " item(s), saved " +
                             SlotVault.Ago(_known[i].WhenUtc) + "  [" + _known[i].utc + "]");
                    Tell("'nvlb.slots.vault restore [n]' puts one back (1 = newest). Refreshing...");
                }
                else Tell("asking the server what it holds for this character...");
                RequestList();
                return;
            }

            int which = 1;
            if (args.Length > 2 && !int.TryParse(args[2], out which)) which = 1;
            if (which < 1) which = 1;
            Tell("asking the server for vault version " + which + "...");
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcGet, id, which - 1);
        }

        private static void Tell(string s)
        {
            if (_lastArgs != null && _lastArgs.Context != null) _lastArgs.Context.AddString("[SafeSlots] " + s);
            Log.LogInfo("[SafeSlots] " + s);
        }

        // ---- status --------------------------------------------------------------------------------------

        public override string StatusDetail()
        {
            if (NoVikingLeftBehindPlugin.IsServerSide)
                return SlotVault.Summary() + " versionsKept=" + VaultVersions +
                       " minUpload=" + MinUploadInterval.ToString("0") + "s" +
                       " dir=" + SlotVault.Dir +
                       " selfTest=" + (_selfTest != null && _selfTest.Value);

            return "characterBackup=" + (_characterBackup != null && _characterBackup.Value) +
                   " keep=" + KeepBackups +
                   " backupDir=" + (CharacterBackup.Folder ?? "?") +
                   " vaultVersionsSeen=" + _known.Count +
                   " lastUploadHash=" + (_lastSentHash ?? "none");
        }
    }
}
