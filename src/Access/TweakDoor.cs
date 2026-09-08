using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The one door every settings change goes through.
    ///
    /// A player never writes a synced setting. The in-game settings tab sends
    /// <c>NVLB_Tweak(section, key, value)</c> to the server over <see cref="ZRoutedRpc"/>; the
    /// server checks permission, validates the value against the <see cref="ConfigCatalog"/>,
    /// rate-limits the sender, and then **writes the cfg file on disk** (with a timestamped
    /// backup, exactly as <c>cfg.py</c> does). The existing <see cref="ConfigWatcher"/> ->
    /// <c>Config.Reload()</c> -> <c>SettingChanged</c> -> ServerSync path then pushes the new
    /// value to every client, so there is one source of truth (the cfg file) and one mechanism
    /// (ServerSync), and nothing about the existing tooling changes.
    ///
    /// The in-memory entry is set as well, immediately after the file write, so the change is
    /// instant rather than waiting up to a second for the watcher's poll. The watcher's later
    /// reload then finds nothing to do ("reloaded: no changes"), which is exactly right.
    ///
    /// Local (BindLocal) settings never come here: they are the player's own machine's business,
    /// so the tab writes the client's own cfg through <see cref="ApplyLocal"/>.
    ///
    /// Everything a player can do here is announced to everyone and logged, which is the design:
    /// the guard rail against a bad afternoon is visibility plus <see cref="Undo"/>, not a lock.
    /// </summary>
    internal static class TweakDoor
    {
        public const string RpcTweak = "NVLB_Tweak";
        public const string RpcUndo = "NVLB_Undo";
        public const string RpcReset = "NVLB_ResetModule";
        public const string RpcAuditReq = "NVLB_AuditRequest";
        public const string RpcResult = "NVLB_TweakResult";
        public const string RpcAudit = "NVLB_Audit";

        private const int RateWindowSeconds = 10;
        private const int UndoDepthPerKey = 20;
        private const int UndoDepthTotal = 100;
        private const int AuditKeep = 20;
        public const int AuditShown = 5;

        // ---- server state -------------------------------------------------------------------

        private sealed class Change
        {
            public string Id;          // "Section.Key"
            public string OldValue;
            public string NewValue;
            public string Actor;
            public DateTime When;
        }

        private static readonly Dictionary<long, List<DateTime>> _rate = new Dictionary<long, List<DateTime>>();
        private static readonly Dictionary<string, List<string>> _undoByKey =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Change> _undoOrder = new List<Change>();
        private static readonly List<string> _audit = new List<string>();

        /// <summary>Set once per ZRoutedRpc instance so a re-register cannot throw.</summary>
        private static object _registeredOn;

        // ---- client state -------------------------------------------------------------------

        /// <summary>Last result from the server, for the tab's status strip. (ok, message)</summary>
        public static event Action<bool, string> Result;

        /// <summary>Fires when the audit list changed, so the tab can redraw its header.</summary>
        public static event Action AuditChanged;

        private static readonly List<string> _clientAudit = new List<string>();

        public static IList<string> AuditLines { get { return _clientAudit; } }

        // ---- registration -------------------------------------------------------------------

        /// <summary>
        /// Called from a postfix on <c>Game.Start</c>, which is where vanilla registers its own
        /// routed RPCs and the first point at which <c>ZRoutedRpc.instance</c> is guaranteed to
        /// exist. <c>ZRoutedRpc</c> is rebuilt on every <c>ZNet.Awake</c> (every world load), and
        /// <c>Register</c> throws on a duplicate name, so we key the guard on the instance itself
        /// rather than on a one-shot bool.
        /// </summary>
        public static void RegisterRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            if (ReferenceEquals(_registeredOn, rpc)) return;
            _registeredOn = rpc;

            _rate.Clear();
            _undoByKey.Clear();
            _undoOrder.Clear();
            _audit.Clear();
            _clientAudit.Clear();

            try
            {
                // Both halves register both directions: a listen-server (a player hosting) is
                // client and server at once, and the routed-RPC layer delivers a message aimed at
                // "the server" to the local handler when that is us.
                rpc.Register<string, string, string>(RpcTweak, RPC_Tweak);
                rpc.Register<string>(RpcUndo, RPC_Undo);
                rpc.Register<string>(RpcReset, RPC_ResetModule);
                rpc.Register(RpcAuditReq, new Action<long>(RPC_AuditRequest));
                rpc.Register<int, string>(RpcResult, RPC_Result);
                rpc.Register<string>(RpcAudit, RPC_Audit);
                Log("registered RPCs " + RpcTweak + " / " + RpcUndo + " / " + RpcReset +
                    " / " + RpcAuditReq + " / " + RpcResult + " / " + RpcAudit);
            }
            catch (Exception e)
            {
                _registeredOn = null;
                NoVikingLeftBehindPlugin.Log.LogError("[Access] could not register RPCs: " + e);
            }
        }

        private static void Log(string s) { NoVikingLeftBehindPlugin.Log.LogInfo("[Access] " + s); }

        // ---- client -> server ---------------------------------------------------------------

        /// <summary>Ask the server to change a synced setting. Client side.</summary>
        public static void Request(SettingInfo info, string value)
        {
            if (info == null) return;
            if (info.IsLocal) { ApplyLocal(info, value); return; }
            if (ZRoutedRpc.instance == null) { Fire(false, "Not connected to a server."); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcTweak, info.Section, info.Key, value);
        }

        /// <summary>Ask the server to undo the most recent change. Client side.</summary>
        public static void RequestUndo()
        {
            if (ZRoutedRpc.instance == null) { Fire(false, "Not connected to a server."); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcUndo, "last");
        }

        /// <summary>Ask the server to put one module's settings back to their defaults.</summary>
        public static void RequestResetModule(string section)
        {
            if (ZRoutedRpc.instance == null) { Fire(false, "Not connected to a server."); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcReset, section);
        }

        /// <summary>Ask the server for the last few audit lines (on tab open).</summary>
        public static void RequestAudit()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcAuditReq);
        }

        /// <summary>
        /// A per-player setting: write this machine's own cfg. Never leaves the client, and never
        /// touches ServerSync - a BindLocal entry is deliberately not registered with it.
        /// </summary>
        public static void ApplyLocal(SettingInfo info, string value)
        {
            object parsed;
            string bad = SettingValue.TryParse(info, value, out parsed);
            if (bad != null) { Fire(false, info.Label + ": " + bad); return; }

            string before = info.CurrentString;
            try
            {
                WriteAndSet(info, parsed, value);
                Fire(true, info.Label + ": " + before + " -> " + info.CurrentString);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Access] local write of " + info.Id + " failed: " + e);
                Fire(false, "Could not save that: " + e.Message);
            }
        }

        // ---- server handlers ------------------------------------------------------------------

        private static void RPC_Tweak(long sender, string section, string key, string value)
        {
            if (!ServerHere()) return;
            try
            {
                string message;
                bool ok = ApplyOne(sender, section, key, value, out message);
                Reply(sender, ok, message);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Access] tweak failed: " + e);
                Reply(sender, false, "The server could not apply that change.");
            }
        }

        private static void RPC_Undo(long sender, string what)
        {
            if (!ServerHere()) return;
            try
            {
                string message;
                bool ok = Undo(sender, out message);
                Reply(sender, ok, message);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Access] undo failed: " + e);
                Reply(sender, false, "The server could not undo that.");
            }
        }

        private static void RPC_ResetModule(long sender, string section)
        {
            if (!ServerHere()) return;
            try
            {
                string message;
                bool ok = ResetModule(sender, section, out message);
                Reply(sender, ok, message);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Access] reset failed: " + e);
                Reply(sender, false, "The server could not reset that module.");
            }
        }

        private static void RPC_AuditRequest(long sender)
        {
            if (!ServerHere()) return;
            SendAudit(sender);
        }

        private static bool ServerHere()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        // ---- server -> client -------------------------------------------------------------------

        private static void Reply(long target, bool ok, string message)
        {
            if (string.IsNullOrEmpty(message)) message = ok ? "Done." : "No.";
            if (ZNet.instance != null && target == ZNet.GetUID())
            {
                // A listen-server changing its own setting: no round trip needed.
                Fire(ok, message);
                return;
            }
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcResult, ok ? 1 : 0, message);
        }

        private static void SendAudit(long target)
        {
            var lines = new List<string>();
            int from = Math.Max(0, _audit.Count - AuditShown);
            for (int i = from; i < _audit.Count; i++) lines.Add(_audit[i]);
            string joined = string.Join("\n", lines.ToArray());

            if (ZNet.instance != null && target == ZNet.GetUID()) { TakeAudit(joined); return; }
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcAudit, joined);
        }

        private static void BroadcastAudit()
        {
            if (ZNet.instance == null) return;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                SendAudit(peer.m_uid);
            }
            if (!NoVikingLeftBehindPlugin.IsServerSide) TakeAudit(null);
        }

        private static void RPC_Result(long sender, int ok, string message)
        {
            Fire(ok != 0, message);
        }

        private static void RPC_Audit(long sender, string lines)
        {
            TakeAudit(lines);
        }

        private static void TakeAudit(string joined)
        {
            _clientAudit.Clear();
            if (!string.IsNullOrEmpty(joined))
                _clientAudit.AddRange(joined.Split('\n'));
            var h = AuditChanged;
            if (h != null) { try { h(); } catch { /* the UI is never allowed to break the door */ } }
        }

        private static void Fire(bool ok, string message)
        {
            var h = Result;
            if (h != null) { try { h(ok, message); } catch { /* ditto */ } }
            if (!ok) NoVikingLeftBehindPlugin.Log.LogInfo("[Access] " + message);
        }

        // ---- the actual work (server) -------------------------------------------------------------

        internal static bool ApplyOne(long sender, string section, string key, string value, out string message)
        {
            var info = ConfigCatalog.Find(section, key);
            if (info == null) { message = "There is no setting called " + section + "." + key + "."; return false; }

            string denied = Permission(sender, info, value);
            if (denied != null) { message = denied; return false; }

            object parsed;
            string bad = SettingValue.TryParse(info, value, out parsed);
            if (bad != null) { message = info.Label + ": " + bad; return false; }

            string canonical = SettingValue.Format(info, parsed);
            string before = info.CurrentString;
            if (canonical == before) { message = info.Label + " is already " + canonical + "."; return true; }

            if (!TakeRateToken(sender))
            {
                message = "Slow down - that is more than " + AccessModule.RateLimit +
                          " changes in " + RateWindowSeconds + " seconds.";
                return false;
            }

            WriteAndSet(info, parsed, canonical);
            RecordUndo(info.Id, before, canonical, ActorName(sender));
            AnnounceChange(sender, info, before, canonical);

            message = info.Label + ": " + before + " -> " + canonical;
            return true;
        }

        internal static bool Undo(long sender, out string message)
        {
            if (_undoOrder.Count == 0) { message = "Nothing to undo."; return false; }

            var change = _undoOrder[_undoOrder.Count - 1];
            var info = ConfigCatalog.Find(change.Id);
            if (info == null)
            {
                _undoOrder.RemoveAt(_undoOrder.Count - 1);
                message = "That setting no longer exists.";
                return false;
            }

            string denied = Permission(sender, info, change.OldValue);
            if (denied != null) { message = denied; return false; }
            if (!TakeRateToken(sender)) { message = "Slow down."; return false; }

            object parsed;
            string bad = SettingValue.TryParse(info, change.OldValue, out parsed);
            if (bad != null) { message = "Cannot undo: " + bad; return false; }

            string before = info.CurrentString;
            _undoOrder.RemoveAt(_undoOrder.Count - 1);
            List<string> stack;
            if (_undoByKey.TryGetValue(change.Id, out stack) && stack.Count > 0)
                stack.RemoveAt(stack.Count - 1);

            WriteAndSet(info, parsed, change.OldValue);
            AnnounceChange(sender, info, before, change.OldValue, "put back");

            message = "Undone: " + info.Label + " back to " + change.OldValue + ".";
            return true;
        }

        private static bool ResetModule(long sender, string section, out string message)
        {
            var edits = new List<KeyValuePair<string, string>>();
            var applied = new List<SettingInfo>();
            var befores = new List<string>();
            int skipped = 0;

            foreach (var info in ConfigCatalog.All)
            {
                if (!string.Equals(info.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (info.IsLocal) continue;

                string denied = Permission(sender, info, info.DefaultString);
                if (denied != null) { skipped++; continue; }
                if (info.CurrentString == info.DefaultString) continue;

                object parsed;
                if (SettingValue.TryParse(info, info.DefaultString, out parsed) != null) { skipped++; continue; }

                edits.Add(new KeyValuePair<string, string>(info.Id, info.DefaultString));
                applied.Add(info);
                befores.Add(info.CurrentString);
            }

            if (edits.Count == 0)
            {
                message = skipped > 0
                    ? "Nothing you can change in " + section + " is off its default."
                    : section + " is already at its defaults.";
                return skipped == 0;
            }

            if (!TakeRateToken(sender)) { message = "Slow down."; return false; }

            // One backup, one write, then push each value in memory so ServerSync broadcasts them.
            string backup = CfgFile.SetValues(NoVikingLeftBehindPlugin.Cfg.ConfigFilePath, edits);
            for (int i = 0; i < applied.Count; i++)
            {
                object parsed;
                SettingValue.TryParse(applied[i], applied[i].DefaultString, out parsed);
                SetInMemory(applied[i], parsed);
                RecordUndo(applied[i].Id, befores[i], applied[i].DefaultString, ActorName(sender));
            }

            string line = "[NVLB] " + ActorName(sender) + " reset " + section + " to defaults (" +
                          edits.Count + " settings)";
            NoVikingLeftBehindPlugin.Log.LogInfo(line + "  backup=" + backup);
            PushAudit(line);
            Broadcast(sender, "[NVLB] reset " + section + " to defaults (" + edits.Count + " settings)", line);

            message = "Reset " + edits.Count + " settings in " + section + " to their defaults." +
                      (skipped > 0 ? " " + skipped + " needed an admin and were left alone." : "");
            return true;
        }

        // ---- permission ----------------------------------------------------------------------------

        /// <summary>Null when allowed, otherwise the sentence to show the player.</summary>
        private static string Permission(long sender, SettingInfo info, string newValue)
        {
            if (!AccessModule.DoorOpen)
                return "The in-game settings menu is switched off on this server.";
            if (info.IsLocal)
                return "That setting is per-player and is saved on your own machine.";

            bool admin = IsAdmin(sender);

            if (AccessModule.AdminsOnly && !admin)
                return "Only server admins can change settings on this server.";
            if (info.Tier == SettingTier.Admin && !admin)
                return "Only a server admin can change " + info.Label + ".";
            if (!info.Live)
                return "Needs a server restart.";

            if (info.IsEnabledToggle && info.Owner != null)
            {
                bool wantOn;
                if (SettingValue.TryParseBool(newValue ?? "", out wantOn) && wantOn && !info.Owner.BootEnabled)
                    return "Needs a server restart.";
            }

            return null;
        }

        /// <summary>
        /// adminlist.txt, the way vanilla asks it: the peer's platform id through
        /// <c>ZNet.IsAdmin</c> (which normalises "Steam_765..." and the bare SteamID64 form).
        /// The server itself (a listen-server host) is always allowed.
        /// </summary>
        private static bool IsAdmin(long sender)
        {
            var net = ZNet.instance;
            if (net == null) return false;
            if (sender == ZNet.GetUID()) return true;
            var peer = net.GetPeer(sender);
            if (peer == null || peer.m_socket == null) return false;
            try { return net.IsAdmin(peer.m_socket.GetHostName()); }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Access] admin check failed: " + e.Message);
                return false;
            }
        }

        private static bool TakeRateToken(long sender)
        {
            var now = DateTime.UtcNow;
            List<DateTime> hits;
            if (!_rate.TryGetValue(sender, out hits)) { hits = new List<DateTime>(); _rate[sender] = hits; }
            hits.RemoveAll(t => (now - t).TotalSeconds > RateWindowSeconds);
            if (hits.Count >= AccessModule.RateLimit) return false;
            hits.Add(now);
            return true;
        }

        // ---- applying ---------------------------------------------------------------------------------

        /// <summary>
        /// Write the cfg file (with a cfg.py-style backup), then set the entry in memory so the
        /// change is instant. Setting it raises SettingChanged, which is what ServerSync listens
        /// on to broadcast to every client - see Vendor/ServerSync.cs (AddConfigEntry).
        /// </summary>
        private static void WriteAndSet(SettingInfo info, object parsed, string canonical)
        {
            string path = NoVikingLeftBehindPlugin.Cfg.ConfigFilePath;
            string backup = null;
            try
            {
                backup = CfgFile.SetValue(path, info.Section, info.Key, canonical);
            }
            catch (Exception e)
            {
                // The file is the source of truth, so a failed write must not leave memory ahead
                // of it - a later Config.Reload() would silently revert the change.
                NoVikingLeftBehindPlugin.Log.LogError("[Access] could not write " + path + ": " + e.Message);
                throw;
            }

            SetInMemory(info, parsed);
            NoVikingLeftBehindPlugin.Log.LogInfo("[Access] wrote [" + info.Section + "] " + info.Key +
                                                 " = " + canonical + "  (backup " + backup + ")");
        }

        private static void SetInMemory(SettingInfo info, object parsed)
        {
            var cfg = NoVikingLeftBehindPlugin.Cfg;
            bool wasSaveOnSet = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;   // we already wrote the file ourselves, line by line
            try { info.Entry.BoxedValue = parsed; }
            finally { cfg.SaveOnConfigSet = wasSaveOnSet; }
        }

        // ---- undo history ----------------------------------------------------------------------------

        private static void RecordUndo(string id, string oldValue, string newValue, string actor)
        {
            List<string> stack;
            if (!_undoByKey.TryGetValue(id, out stack)) { stack = new List<string>(); _undoByKey[id] = stack; }
            stack.Add(oldValue);
            while (stack.Count > UndoDepthPerKey) stack.RemoveAt(0);

            _undoOrder.Add(new Change
            {
                Id = id,
                OldValue = oldValue,
                NewValue = newValue,
                Actor = actor,
                When = DateTime.UtcNow
            });
            while (_undoOrder.Count > UndoDepthTotal) _undoOrder.RemoveAt(0);
        }

        // ---- audit / announce -------------------------------------------------------------------------

        private static void AnnounceChange(long sender, SettingInfo info, string before, string after,
                                     string verb = "set")
        {
            string actor = ActorName(sender);
            string what = info.Section + "." + info.Key;

            // The chat window prefixes the speaker's own name, so the chat body omits it; the log
            // line and the message-hud fallback carry it. ">" is stripped by vanilla's chat
            // handler, hence "to" rather than "->" in the chat body.
            string logLine = "[NVLB] " + actor + " " + verb + " " + what + " " + before + " -> " + after;
            string chatBody = "[NVLB] " + verb + " " + what + " " + before + " to " + after;

            NoVikingLeftBehindPlugin.Log.LogInfo(logLine);
            PushAudit(logLine);
            Broadcast(sender, chatBody, logLine);
        }

        private static void PushAudit(string line)
        {
            _audit.Add(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + line);
            while (_audit.Count > AuditKeep) _audit.RemoveAt(0);
            BroadcastAudit();
        }

        /// <summary>
        /// One line, seen by everyone. Chat is the default (Matt's call): a dedicated server has
        /// no character of its own, so the line borrows the acting player's identity from the
        /// server's own player list, which is exactly what vanilla's chat handler looks the
        /// speaker up in - the client renders it as "Erik: [NVLB] set ...". If that player is not
        /// (yet) in the list, or chat is switched off, it falls back to MessageHud's top-left
        /// message, which needs no identity at all.
        /// </summary>
        private static void Broadcast(long sender, string chatBody, string hudLine)
        {
            var mode = AccessModule.AnnounceMode;
            if (mode == Announce.Off) return;

            bool spoke = false;
            if (mode == Announce.Chat || mode == Announce.Both) spoke = TrySayInChat(sender, chatBody);
            if (mode == Announce.Message || mode == Announce.Both || !spoke) ShowMessageAll(hudLine);
        }

        private static bool TrySayInChat(long sender, string body)
        {
            try
            {
                var net = ZNet.instance;
                var rpc = ZRoutedRpc.instance;
                if (net == null || rpc == null) return false;

                var players = net.GetPlayerList();
                if (players == null) return false;

                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p.m_characterID.UserID != sender) continue;
                    if (!p.m_userInfo.m_id.IsValid) return false;

                    var who = new UserInfo();
                    who.Name = p.m_name;
                    who.UserId = p.m_userInfo.m_id;

                    var peer = net.GetPeer(sender);
                    Vector3 at = peer != null ? peer.m_refPos : p.m_position;

                    rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ChatMessage",
                                        at, (int)Talker.Type.Normal, who, body);
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Access] chat announce failed: " + e.Message);
                return false;
            }
        }

        private static void ShowMessageAll(string line)
        {
            try
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage",
                                                    (int)MessageHud.MessageType.TopLeft, line);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Access] message announce failed: " + e.Message);
            }
        }

        /// <summary>The player's character name, never anything that could identify a real person
        /// beyond the name they chose in game. Unknown peers become "someone".</summary>
        private static string ActorName(long sender)
        {
            var net = ZNet.instance;
            if (net == null) return "someone";
            if (sender == ZNet.GetUID()) return "the server";
            var peer = net.GetPeer(sender);
            if (peer != null && !string.IsNullOrEmpty(peer.m_playerName)) return peer.m_playerName;
            return "someone";
        }

        // ---- server-side introspection for nvlb.status ------------------------------------------------

        public static string StatusLine()
        {
            return "changes=" + _undoOrder.Count + " undoable, audit=" + _audit.Count + " lines";
        }

        public static IList<string> ServerAudit() { return _audit; }
    }
}
