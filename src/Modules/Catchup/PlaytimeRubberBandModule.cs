using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Module 6 of the catch-up set: PlaytimeRubberBand. Side = Both.
    ///
    /// PRINCIPLE (CATCHUP-SPEC.md): never touch the frontier, soften the trail behind it. Nobody
    /// is slowed down; players with less connected time than the group median get a small, capped
    /// bonus to gathering and skill XP so an evening of catching up is actually possible.
    ///
    /// SERVER half
    ///   * Tracks connected seconds per player, keyed "steamid|charactername"
    ///     (ZNetPeer.m_socket.GetHostName() + ZNetPeer.m_playerName).
    ///   * Hooks:
    ///       - postfix ZNet.RPC_PeerInfo(ZRpc, ZPackage)  : a peer has finished the handshake and
    ///         now has m_uid / m_playerName set. Starts its accrual clock. (ZNet.decompiled.cs:947
    ///         sets m_playerName; m_zdoMan.AddPeer/m_routedRpc.AddPeer are the last statements.)
    ///       - prefix  ZNet.Disconnect(ZNetPeer)          : flush the partial interval and save.
    ///         Disconnect is the single funnel - ZNet.Update's timeout path, RPC_Disconnect and
    ///         Shutdown all reach it (ZNet.decompiled.cs:1042).
    ///       - postfix ZNet.Update()                      : the tick. Every RecomputeSec (60 s) we
    ///         accrue for every currently-connected peer and recompute, so a server crash loses at
    ///         most one interval instead of a whole session.
    ///   * Persists to <Paths.ConfigPath>/nvlb/playtime.json (i.e. /config/bepinex/nvlb/).
    ///   * Every recompute: players seen within WindowDays form the group; if there are at least
    ///     MinGroupSize of them we take the median hours; every connected player below the median
    ///     gets factor = 1 + min(MaxBonus, (median - hours) / median * MaxBonus). Everyone else
    ///     gets 1.0, which is also what is pushed when the group shrinks below MinGroupSize, so
    ///     the bonus always resets itself.
    ///   * Sends the routed RPC "NVLB_Catchup"(float gather, float xp) to that one peer.
    ///
    /// CLIENT half
    ///   * Receives "NVLB_Catchup" and stores the two factors.
    ///   * xp   : prefix on Skills.RaiseSkill(SkillType, float factor) multiplies `factor`.
    ///            GroupSkillCatchup installs a SEPARATE prefix on the same method with its own
    ///            Harmony instance; both multiply the same ref parameter, so the two bonuses
    ///            compose multiplicatively and the order they run in does not matter.
    ///   * gather: postfix on DropTable.GetDropList() (the parameterless public overload) and on
    ///            CharacterDrop.GenerateDropList(). GetDropList is the narrowest single point that
    ///            covers ore (MineRock5.DamageArea, MineRock5.decompiled.cs:394), trees/rocks/
    ///            destructibles (DropOnDestroyed.OnDestroyed -> m_dropWhenDestroyed.GetDropList())
    ///            and TreeLog; GenerateDropList covers creature loot.
    ///
    ///     APPROXIMATION, documented deliberately: both of those only ever run on the machine that
    ///     OWNS the object being destroyed, and in a normal session that is the client standing next
    ///     to it - the one who swung the pickaxe. We do not try to identify the killer from
    ///     Character.m_lastHit; we simply scale drops produced on this client while the local player
    ///     exists. Consequence: if you own a zone and something dies in it that a team-mate killed,
    ///     your factor is the one applied. With a capped, small bonus this is harmless, and it is
    ///     the only way to stay inside one narrow patch.
    ///
    ///   * Fractional factors use probabilistic rounding (CatchupUtil.ProbRound) so a x1.3 bonus on
    ///     a single-item drop pays out 30% of the time instead of rounding away to nothing.
    /// </summary>
    internal sealed class PlaytimeRubberBandModule : FeatureModule
    {
        public override string Name => "PlaytimeRubberBand";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "Playtime";
        public override string Theme => "Catching up";
        public override string Hint => "Extra gathering and XP for players who played less";

        protected override string EnabledDescription =>
            "Give players with less connected time than the group median a small, capped bonus " +
            "to gathering and skill XP. Never slows anyone down.";

        internal const string RpcCatchup = "NVLB_Catchup";

        private const string StateFile = "playtime.json";

        private static PlaytimeRubberBandModule _self;

        // ---- config -------------------------------------------------------------------

        private ConfigEntry<int> _recomputeSec;
        private ConfigEntry<int> _windowDays;
        private ConfigEntry<int> _minGroupSize;
        private ConfigEntry<float> _maxBonus;
        private ConfigEntry<bool> _gatherBonusEnabled;
        private ConfigEntry<bool> _xpBonusEnabled;

        // ---- server state ---------------------------------------------------------------

        private sealed class Rec
        {
            public string Key;
            public string SteamId;
            public string Name;
            public double Seconds;
            public long LastSeen;
            public double Hours => Seconds / 3600.0;
        }

        private readonly Dictionary<string, Rec> _players = new Dictionary<string, Rec>();
        /// <summary>peer uid -> unix time we last accrued for it.</summary>
        private readonly Dictionary<long, long> _accrualMark = new Dictionary<long, long>();
        /// <summary>peer uid -> last (gather, xp) we pushed, so we only send on change.</summary>
        private readonly Dictionary<long, KeyValuePair<float, float>> _sent =
            new Dictionary<long, KeyValuePair<float, float>>();

        private bool _loaded;
        private bool _dirty;
        private float _nextTick;
        private bool _loggedOnce;
        private double _lastMedian = -1;
        private bool _selfTestDone;

        // ---- client state ---------------------------------------------------------------

        private static float _gatherFactor = 1f;
        private static float _xpFactor = 1f;

        private static object _rpcRegisteredOn;

        // ---- lifecycle -------------------------------------------------------------------

        protected override void Bind()
        {
            _recomputeSec = BindSynced("RecomputeSec", 60,
                "Seconds between playtime accrual + recompute passes on the server.",
                Opt.N("How often the server recalculates playtime bonuses", 5, 600));
            _windowDays = BindSynced("WindowDays", 14,
                "Only players seen within this many days count towards the group median.",
                Opt.N("How many recent days count towards the group's playtime", 1, 60));
            _minGroupSize = BindSynced("MinGroupSize", 3,
                "Below this many players in the window, no bonus is handed out at all.",
                Opt.N("Minimum group size before any catch-up bonus applies", 1, 20));
            _maxBonus = BindSynced("MaxBonus", 1.0f,
                "Maximum bonus. 1.0 = at most double rate for the furthest-behind player.",
                Opt.N("Biggest possible gathering and XP bonus", 0, 5, 0.1));
            _gatherBonusEnabled = BindSynced("GatherBonusEnabled", true,
                "Apply the catch-up factor to item drops the client produces (ore, wood, loot).",
                Opt.B("Give the furthest-behind players extra item drops"));
            _xpBonusEnabled = BindSynced("XpBonusEnabled", true,
                "Apply the catch-up factor to skill XP gain.",
                Opt.B("Give the furthest-behind players extra skill XP"));

            if (CatchupUtil.SelfTestCfg == null)
                CatchupUtil.SelfTestCfg = BindLocal("Catchup", "SelfTest", false,
                    "LOCAL diagnostic. Seeds fake playtime and skill data once, logs the computed " +
                    "median / factors / ceilings, then does nothing more. Never sync this on.",
                    Opt.B("Run a one-time diagnostic test with fake data").Admin());
        }

        protected override void ApplyPatches()
        {
            var awake = AccessTools.Method(typeof(ZNet), "Awake");
            if (awake == null) throw new Exception("ZNet.Awake() not found");
            Harmony.Patch(awake, postfix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(ZNetAwakePostfix)));

            var update = AccessTools.Method(typeof(ZNet), "Update");
            if (update == null) throw new Exception("ZNet.Update() not found");
            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(ZNetUpdatePostfix)));

            var peerInfo = AccessTools.Method(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) });
            if (peerInfo == null) throw new Exception("ZNet.RPC_PeerInfo(ZRpc, ZPackage) not found");
            Harmony.Patch(peerInfo, postfix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(PeerInfoPostfix)));

            var disconnect = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
            if (disconnect == null) throw new Exception("ZNet.Disconnect(ZNetPeer) not found");
            Harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(DisconnectPrefix)));

            var raise = AccessTools.Method(typeof(Skills), "RaiseSkill", new[] { typeof(Skills.SkillType), typeof(float) });
            if (raise == null) throw new Exception("Skills.RaiseSkill(SkillType, float) not found");
            Harmony.Patch(raise, prefix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(RaiseSkillPrefix)));

            var dropList = AccessTools.Method(typeof(DropTable), "GetDropList", Type.EmptyTypes);
            if (dropList == null) throw new Exception("DropTable.GetDropList() not found");
            Harmony.Patch(dropList, postfix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(GetDropListPostfix)));

            var charDrop = AccessTools.Method(typeof(CharacterDrop), "GenerateDropList", Type.EmptyTypes);
            if (charDrop == null) throw new Exception("CharacterDrop.GenerateDropList() not found");
            Harmony.Patch(charDrop, postfix: new HarmonyMethod(typeof(PlaytimeRubberBandModule), nameof(GenerateDropListPostfix)));

            _self = this;
        }

        public override void Disable()
        {
            try { if (_self == this && _dirty) Save(); } catch { }
            _gatherFactor = 1f;
            _xpFactor = 1f;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            // Every number is read fresh on each tick / patch body, so a change is live at once.
            // Force the next tick to happen immediately so a new value shows up without waiting.
            _nextTick = 0f;
            Log.LogInfo("[Playtime] " + entry.Definition.Key + " = " + entry.BoxedValue);
        }

        public override string StatusDetail()
        {
            var sb = new StringBuilder();
            if (NoVikingLeftBehindPlugin.IsServerSide)
            {
                sb.Append("tracked=").Append(_players.Count);
                sb.Append(" window=").Append(_windowDays.Value).Append("d");
                sb.Append(" minGroup=").Append(_minGroupSize.Value);
                sb.Append(" maxBonus=").Append(CatchupUtil.F(_maxBonus.Value));
                if (_lastMedian >= 0) sb.Append(" median=").Append(CatchupUtil.F(_lastMedian)).Append("h");
            }
            else
            {
                sb.Append("gather=x").Append(CatchupUtil.F(_gatherFactor));
                sb.Append(" xp=x").Append(CatchupUtil.F(_xpFactor));
                if (!_gatherBonusEnabled.Value) sb.Append(" (gather off)");
                if (!_xpBonusEnabled.Value) sb.Append(" (xp off)");
            }
            return sb.ToString();
        }

        // ---- RPC registration --------------------------------------------------------------

        private static void ZNetAwakePostfix()
        {
            if (_self == null || !_self.Active) return;
            try
            {
                var rrpc = ZRoutedRpc.instance;
                if (rrpc == null || ReferenceEquals(rrpc, _rpcRegisteredOn)) return;
                rrpc.Register<float, float>(RpcCatchup, RPC_Catchup);
                _rpcRegisteredOn = rrpc;
                _self._loaded = false;
                _self._players.Clear();
                _self._accrualMark.Clear();
                _self._sent.Clear();
                _gatherFactor = 1f;
                _xpFactor = 1f;
                Log.LogInfo("[Playtime] routed RPC '" + RpcCatchup + "' registered");
            }
            catch (Exception e) { Log.LogError("[Playtime] RPC register failed: " + e); }
        }

        /// <summary>Client: the server has told us our current catch-up factors.</summary>
        private static void RPC_Catchup(long sender, float gather, float xp)
        {
            if (_self == null || !_self.Active) return;
            if (float.IsNaN(gather) || float.IsNaN(xp)) return;
            _gatherFactor = Mathf.Clamp(gather, 1f, 10f);
            _xpFactor = Mathf.Clamp(xp, 1f, 10f);
            Log.LogInfo("[Playtime] catch-up factors from server: gather=x" + CatchupUtil.F(_gatherFactor) +
                        " xp=x" + CatchupUtil.F(_xpFactor));
        }

        // ---- server: peer lifecycle ----------------------------------------------------------

        private static void PeerInfoPostfix(ZNet __instance, ZRpc rpc)
        {
            if (_self == null || !_self.Active || !ServerActive()) return;
            try
            {
                var peer = __instance.GetPeer(rpc);
                if (peer == null || !peer.IsReady()) return;      // handshake failed / rejected
                var key = CatchupUtil.PeerKey(peer);
                if (key == null) return;
                _self.EnsureLoaded();
                var rec = _self.Get(key, CatchupUtil.PeerHost(peer), peer.m_playerName);
                rec.LastSeen = CatchupUtil.NowUnix();
                _self._accrualMark[peer.m_uid] = rec.LastSeen;
                _self._dirty = true;
                Log.LogInfo("[Playtime] " + peer.m_playerName + " joined (" +
                            CatchupUtil.F(rec.Hours) + " h tracked)");
                _self._nextTick = 0f;   // recompute promptly so the newcomer gets its factor
            }
            catch (Exception e) { Log.LogWarning("[Playtime] join hook failed: " + e.Message); }
        }

        private static void DisconnectPrefix(ZNetPeer peer)
        {
            if (_self == null || !_self.Active || !ServerActive()) return;
            try
            {
                if (peer == null) return;
                _self.Accrue(peer);
                _self._accrualMark.Remove(peer.m_uid);
                _self._sent.Remove(peer.m_uid);
                if (_self._dirty) _self.Save();
            }
            catch (Exception e) { Log.LogWarning("[Playtime] leave hook failed: " + e.Message); }
        }

        // ---- server: the tick ------------------------------------------------------------------

        private static void ZNetUpdatePostfix()
        {
            if (_self == null || !_self.Active) return;
            if (!ServerActive()) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < _self._nextTick) return;
                int interval = Mathf.Max(5, _self._recomputeSec.Value);
                _self._nextTick = now + interval;

                _self.EnsureLoaded();
                _self.RunSelfTestOnce();
                _self.AccrueAll();
                _self.Recompute();
                if (_self._dirty) _self.Save();
            }
            catch (Exception e) { Log.LogError("[Playtime] tick failed: " + e); }
        }

        private Rec Get(string key, string steamId, string name)
        {
            Rec r;
            if (!_players.TryGetValue(key, out r))
            {
                r = new Rec { Key = key, SteamId = steamId, Name = name, Seconds = 0, LastSeen = CatchupUtil.NowUnix() };
                _players[key] = r;
            }
            return r;
        }

        private void AccrueAll()
        {
            var znet = ZNet.instance;
            if (znet == null) return;
            foreach (var peer in znet.GetConnectedPeers()) Accrue(peer);
        }

        /// <summary>Add the wall-clock seconds since this peer's last accrual mark to its record.</summary>
        private void Accrue(ZNetPeer peer)
        {
            if (peer == null || !peer.IsReady()) return;
            var key = CatchupUtil.PeerKey(peer);
            if (key == null) return;
            long now = CatchupUtil.NowUnix();
            long mark;
            if (!_accrualMark.TryGetValue(peer.m_uid, out mark)) mark = now;
            long delta = now - mark;
            _accrualMark[peer.m_uid] = now;
            var rec = Get(key, CatchupUtil.PeerHost(peer), peer.m_playerName);
            rec.LastSeen = now;
            if (delta > 0 && delta < 24 * 3600)   // ignore nonsense (clock jump / suspend)
            {
                rec.Seconds += delta;
                _dirty = true;
            }
        }

        private void Recompute()
        {
            var znet = ZNet.instance;
            if (znet == null) return;

            long cutoff = CatchupUtil.NowUnix() - (long)Mathf.Max(1, _windowDays.Value) * 86400L;
            var window = new List<Rec>();
            foreach (var r in _players.Values) if (r.LastSeen >= cutoff) window.Add(r);

            bool first = !_loggedOnce;
            _loggedOnce = true;

            if (window.Count < Mathf.Max(1, _minGroupSize.Value))
            {
                _lastMedian = -1;
                string msg = "[Playtime] group too small (" + window.Count + " < " + _minGroupSize.Value +
                             " in the last " + _windowDays.Value + "d), no bonus";
                if (first) Log.LogInfo(msg); else Log.LogDebug(msg);
                foreach (var peer in znet.GetConnectedPeers()) Push(peer, 1f, 1f);
                return;
            }

            double median = Median(window);
            _lastMedian = median;

            var detail = new StringBuilder();
            detail.Append("[Playtime] median=").Append(CatchupUtil.F(median)).Append("h over ")
                  .Append(window.Count).Append(" players");

            foreach (var peer in znet.GetConnectedPeers())
            {
                if (!peer.IsReady()) continue;
                var key = CatchupUtil.PeerKey(peer);
                if (key == null) continue;
                Rec rec;
                if (!_players.TryGetValue(key, out rec)) continue;

                float factor = FactorFor(rec.Hours, median);
                detail.Append("; ").Append(rec.Name).Append("=").Append(CatchupUtil.F(rec.Hours))
                      .Append("h x").Append(CatchupUtil.F(factor));
                Push(peer, _gatherBonusEnabled.Value ? factor : 1f, _xpBonusEnabled.Value ? factor : 1f);
            }

            if (first) Log.LogInfo(detail.ToString()); else Log.LogDebug(detail.ToString());
        }

        /// <summary>1 + min(MaxBonus, (median - hours) / median * MaxBonus), never below 1.</summary>
        private float FactorFor(double hours, double median)
        {
            if (median <= 0 || hours >= median) return 1f;
            double max = Mathf.Max(0f, _maxBonus.Value);
            double bonus = (median - hours) / median * max;
            if (bonus > max) bonus = max;
            if (bonus < 0) bonus = 0;
            return (float)(1.0 + bonus);
        }

        private static double Median(List<Rec> recs)
        {
            var hours = new List<double>(recs.Count);
            foreach (var r in recs) hours.Add(r.Hours);
            hours.Sort();
            int n = hours.Count;
            if (n == 0) return 0;
            return (n % 2 == 1) ? hours[n / 2] : (hours[n / 2 - 1] + hours[n / 2]) / 2.0;
        }

        private void Push(ZNetPeer peer, float gather, float xp)
        {
            if (peer == null || !peer.IsReady()) return;
            KeyValuePair<float, float> last;
            if (_sent.TryGetValue(peer.m_uid, out last) &&
                Mathf.Abs(last.Key - gather) < 0.001f && Mathf.Abs(last.Value - xp) < 0.001f) return;
            var rrpc = ZRoutedRpc.instance;
            if (rrpc == null) return;
            rrpc.InvokeRoutedRPC(peer.m_uid, RpcCatchup, gather, xp);
            _sent[peer.m_uid] = new KeyValuePair<float, float>(gather, xp);
        }

        // ---- persistence ---------------------------------------------------------------------

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            var path = CatchupUtil.DataFile(StateFile);
            try
            {
                if (!File.Exists(path))
                {
                    CatchupUtil.EnsureDataDir();
                    Save();
                    Log.LogInfo("[Playtime] created " + path);
                    return;
                }
                Load(File.ReadAllText(path));
                Log.LogInfo("[Playtime] loaded " + _players.Count + " player record(s) from " + path);
            }
            catch (Exception e)
            {
                Log.LogError("[Playtime] could not read " + path + ": " + e.Message + " - starting empty");
            }
        }

        private void Load(string json)
        {
            _players.Clear();
            var root = CatchupUtil.AsObj(CatchupUtil.ParseJson(json));
            if (root == null) return;
            object playersObj;
            if (!root.TryGetValue("players", out playersObj)) return;
            var arr = CatchupUtil.AsArr(playersObj);
            if (arr == null) return;
            foreach (var item in arr)
            {
                var o = CatchupUtil.AsObj(item);
                if (o == null) continue;
                var rec = new Rec
                {
                    SteamId = CatchupUtil.Str(o, "steamId", "unknown"),
                    Name = CatchupUtil.Str(o, "name", ""),
                    Seconds = CatchupUtil.Num(o, "seconds", 0),
                    LastSeen = (long)CatchupUtil.Num(o, "lastSeen", 0)
                };
                rec.Key = CatchupUtil.Str(o, "key", rec.SteamId + "|" + rec.Name);
                if (string.IsNullOrEmpty(rec.Key)) continue;
                _players[rec.Key] = rec;
            }
        }

        private void Save()
        {
            var path = CatchupUtil.DataFile(StateFile);
            try
            {
                CatchupUtil.EnsureDataDir();
                var sb = new StringBuilder();
                sb.Append("{\n  \"version\": 1,\n  \"savedUnix\": ").Append(CatchupUtil.NowUnix());
                sb.Append(",\n  \"players\": [");
                bool firstItem = true;
                foreach (var r in _players.Values)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    sb.Append("\n    {\"key\": ");
                    CatchupUtil.AppendString(sb, r.Key);
                    sb.Append(", \"steamId\": ");
                    CatchupUtil.AppendString(sb, r.SteamId ?? "unknown");
                    sb.Append(", \"name\": ");
                    CatchupUtil.AppendString(sb, r.Name ?? "");
                    sb.Append(", \"seconds\": ").Append(r.Seconds.ToString("0.###", CultureInfo.InvariantCulture));
                    sb.Append(", \"lastSeen\": ").Append(r.LastSeen);
                    sb.Append('}');
                }
                sb.Append("\n  ]\n}\n");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _dirty = false;
            }
            catch (Exception e)
            {
                Log.LogError("[Playtime] could not write " + path + ": " + e.Message);
            }
        }

        // ---- client: apply -----------------------------------------------------------------------

        private static bool LocalSkills(Skills s)
        {
            var p = Player.m_localPlayer;
            return p != null && s != null && ReferenceEquals(s.m_player, p);
        }

        /// <summary>
        /// Multiply the XP factor. GroupSkillCatchup has its own, independent prefix on this same
        /// method; both multiply `factor`, so the two effects compose and their order is irrelevant.
        /// </summary>
        private static void RaiseSkillPrefix(Skills __instance, Skills.SkillType skillType, ref float factor)
        {
            if (_self == null || !_self.Active || !ClientActive()) return;
            if (!_self._xpBonusEnabled.Value) return;
            if (_xpFactor <= 1.0001f) return;
            if (skillType == Skills.SkillType.None) return;
            if (!LocalSkills(__instance)) return;
            factor *= _xpFactor;
        }

        private static bool GatherOn()
        {
            return _self != null && _self.Active && ClientActive() &&
                   _self._gatherBonusEnabled.Value && _gatherFactor > 1.0001f &&
                   Player.m_localPlayer != null;
        }

        /// <summary>
        /// Ore / wood / destructible / container loot. The list is one GameObject entry per item,
        /// so scaling = appending copies. Probabilistic rounding keeps the average exact.
        /// </summary>
        private static void GetDropListPostfix(List<GameObject> __result)
        {
            if (__result == null || __result.Count == 0) return;
            if (!GatherOn()) return;
            try
            {
                int orig = __result.Count;
                int target = CatchupUtil.ProbRound(orig * _gatherFactor);
                for (int i = orig; i < target; i++) __result.Add(__result[(i - orig) % orig]);
            }
            catch (Exception e) { Log.LogWarning("[Playtime] drop scale failed: " + e.Message); }
        }

        /// <summary>Creature loot: (prefab, amount) pairs, so scale the amounts.</summary>
        private static void GenerateDropListPostfix(List<KeyValuePair<GameObject, int>> __result)
        {
            if (__result == null || __result.Count == 0) return;
            if (!GatherOn()) return;
            try
            {
                for (int i = 0; i < __result.Count; i++)
                {
                    var kv = __result[i];
                    if (kv.Value <= 0) continue;
                    int n = CatchupUtil.ProbRound(kv.Value * _gatherFactor);
                    if (n < kv.Value) n = kv.Value;      // never take loot away
                    __result[i] = new KeyValuePair<GameObject, int>(kv.Key, n);
                }
            }
            catch (Exception e) { Log.LogWarning("[Playtime] creature drop scale failed: " + e.Message); }
        }

        // ---- self test ------------------------------------------------------------------------------

        /// <summary>
        /// [Catchup] SelfTest = true: seed four fake players (40/35/30/5 h) and log the median and
        /// the factor each of them would get. Local diagnostic only; it writes playtime.json, so
        /// turn it back off and delete the file afterwards.
        /// </summary>
        private void RunSelfTestOnce()
        {
            if (_selfTestDone || !CatchupUtil.SelfTest) return;
            _selfTestDone = true;
            try
            {
                long now = CatchupUtil.NowUnix();
                double[] hours = { 40, 35, 30, 5 };
                string[] names = { "Alfr", "Bjorn", "Cato", "Dagny" };
                _players.Clear();
                for (int i = 0; i < hours.Length; i++)
                {
                    string key = "76561198000000" + (10 + i) + "|" + names[i];
                    _players[key] = new Rec
                    {
                        Key = key,
                        SteamId = "76561198000000" + (10 + i),
                        Name = names[i],
                        Seconds = hours[i] * 3600.0,
                        LastSeen = now
                    };
                }
                _dirty = true;
                Save();

                var window = new List<Rec>(_players.Values);
                double median = Median(window);
                var sb = new StringBuilder();
                sb.Append("[Catchup SelfTest] playtime: ").Append(window.Count).Append(" seeded players, median=")
                  .Append(CatchupUtil.F(median)).Append("h, MinGroupSize=").Append(_minGroupSize.Value)
                  .Append(", MaxBonus=").Append(CatchupUtil.F(_maxBonus.Value));
                Log.LogInfo(sb.ToString());
                foreach (var r in window)
                    Log.LogInfo("[Catchup SelfTest]   " + r.Name + ": " + CatchupUtil.F(r.Hours) +
                                "h -> gather x" + CatchupUtil.F(FactorFor(r.Hours, median)) +
                                " xp x" + CatchupUtil.F(FactorFor(r.Hours, median)));
                Log.LogInfo("[Catchup SelfTest] wrote " + CatchupUtil.DataFile(StateFile) +
                            " - set [Catchup] SelfTest = false and delete that file when done");
            }
            catch (Exception e) { Log.LogError("[Catchup SelfTest] playtime self test failed: " + e); }
        }
    }
}
