using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Module 7 of the catch-up set: GroupSkillCatchup. Side = Both.
    ///
    /// Skills are the other half of falling behind: a player who missed three sessions is not just
    /// short of bronze, their Axes and Blocking are 20 levels under everyone else's and every fight
    /// is harder for them than it is for the group. This module lifts the floor towards the group's
    /// own ceiling and stops exactly there - it never pushes anybody past the best player, and the
    /// best player gets nothing at all. (Distinct from SmartSkills, which compares you to your own
    /// past; this compares you to the people you actually play with.)
    ///
    /// CLIENT half
    ///   * Every ReportSec (60 s), sends "NVLB_SkillReport" to the server: a ZPackage of
    ///     [int count]{[int skillType][float level]} for every skill above 0.
    ///   * Receives "NVLB_SkillCeiling" (same layout) and caches it.
    ///   * Prefix on Skills.RaiseSkill(SkillType, float factor): for a skill below the ceiling,
    ///       factor *= min(MaxFactor, 1 + Bonus * (ceiling - level) / ceiling).
    ///     PlaytimeRubberBand installs its OWN prefix on the same method with its own Harmony
    ///     instance. Both multiply the same `ref factor`, so the two bonuses compose
    ///     multiplicatively and the order Harmony happens to run them in does not matter.
    ///
    /// SERVER half
    ///   * Keeps the latest report per player key ("steamid|charactername") with a timestamp.
    ///   * Group ceiling per skill = max level over all reports newer than WindowDays.
    ///   * Broadcasts "NVLB_SkillCeiling" to everybody when the ceiling changes, and in any case
    ///     every BroadcastSec (300 s) so a client that joined mid-window is never left stale.
    ///
    /// Reports live in memory only: a server restart drops them and every connected client
    /// re-reports within ReportSec, so there is nothing worth persisting.
    ///
    /// Hooks (same three entry points as PlaytimeRubberBand, each module patching with its own
    /// Harmony instance): postfix ZNet.Awake (register the routed RPCs - the server registers at
    /// its own Awake, long before any client can send), postfix ZNet.Update (the tick, both
    /// halves), prefix Skills.RaiseSkill (client).
    /// </summary>
    internal sealed class GroupSkillCatchupModule : FeatureModule
    {
        public override string Name => "GroupSkillCatchup";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "SkillCatchup";

        protected override string EnabledDescription =>
            "Lift skills that are below the group's best towards it. Capped, and the leaders " +
            "themselves get nothing.";

        internal const string RpcReport = "NVLB_SkillReport";
        internal const string RpcCeiling = "NVLB_SkillCeiling";

        private static GroupSkillCatchupModule _self;
        private static object _rpcRegisteredOn;

        // ---- config ---------------------------------------------------------------------

        private ConfigEntry<int> _reportSec;
        private ConfigEntry<int> _windowDays;
        private ConfigEntry<int> _broadcastSec;
        private ConfigEntry<float> _bonus;
        private ConfigEntry<float> _maxFactor;

        // ---- server state ----------------------------------------------------------------

        private sealed class Report
        {
            public string Key;
            public string Name;
            public long When;
            public Dictionary<int, float> Levels = new Dictionary<int, float>();
        }

        private readonly Dictionary<string, Report> _reports = new Dictionary<string, Report>();
        private readonly Dictionary<int, float> _serverCeiling = new Dictionary<int, float>();
        private float _nextBroadcast;
        private bool _loggedOnce;
        private bool _selfTestDone;

        // ---- client state ------------------------------------------------------------------

        private static readonly Dictionary<int, float> _ceiling = new Dictionary<int, float>();
        private float _nextReport;

        // ---- lifecycle -----------------------------------------------------------------------

        protected override void Bind()
        {
            _reportSec = BindSynced("ReportSec", 60,
                "Seconds between a client sending its skill levels to the server.");
            _windowDays = BindSynced("WindowDays", 14,
                "Only reports newer than this many days count towards the group ceiling.");
            _broadcastSec = BindSynced("BroadcastSec", 300,
                "Server re-broadcasts the ceiling at least this often, even when unchanged.");
            _bonus = BindSynced("Bonus", 1.0f,
                "Strength of the catch-up. 1.0 = a skill at half the group ceiling gains 1.5x.");
            _maxFactor = BindSynced("MaxFactor", 3.0f,
                "Hard cap on this module's multiplier, whatever the gap.");

            if (CatchupUtil.SelfTestCfg == null)
                CatchupUtil.SelfTestCfg = BindLocal("Catchup", "SelfTest", false,
                    "LOCAL diagnostic. Seeds fake playtime and skill data once, logs the computed " +
                    "median / factors / ceilings, then does nothing more. Never sync this on.");
        }

        protected override void ApplyPatches()
        {
            var awake = AccessTools.Method(typeof(ZNet), "Awake");
            if (awake == null) throw new Exception("ZNet.Awake() not found");
            Harmony.Patch(awake, postfix: new HarmonyMethod(typeof(GroupSkillCatchupModule), nameof(ZNetAwakePostfix)));

            var update = AccessTools.Method(typeof(ZNet), "Update");
            if (update == null) throw new Exception("ZNet.Update() not found");
            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(GroupSkillCatchupModule), nameof(ZNetUpdatePostfix)));

            var raise = AccessTools.Method(typeof(Skills), "RaiseSkill", new[] { typeof(Skills.SkillType), typeof(float) });
            if (raise == null) throw new Exception("Skills.RaiseSkill(SkillType, float) not found");
            Harmony.Patch(raise, prefix: new HarmonyMethod(typeof(GroupSkillCatchupModule), nameof(RaiseSkillPrefix)));

            var disconnect = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
            if (disconnect == null) throw new Exception("ZNet.Disconnect(ZNetPeer) not found");
            Harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(GroupSkillCatchupModule), nameof(DisconnectPrefix)));

            _self = this;
        }

        public override void Disable()
        {
            _ceiling.Clear();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            _nextReport = 0f;
            _nextBroadcast = 0f;
            Log.LogInfo("[SkillCatchup] " + entry.Definition.Key + " = " + entry.BoxedValue);
        }

        public override string StatusDetail()
        {
            var sb = new StringBuilder();
            if (NoVikingLeftBehindPlugin.IsServerSide)
            {
                sb.Append("reports=").Append(_reports.Count)
                  .Append(" ceilings=").Append(_serverCeiling.Count)
                  .Append(" window=").Append(_windowDays.Value).Append("d")
                  .Append(" bonus=").Append(CatchupUtil.F(_bonus.Value))
                  .Append(" max=x").Append(CatchupUtil.F(_maxFactor.Value));
            }
            else
            {
                sb.Append("ceilings=").Append(_ceiling.Count)
                  .Append(" bonus=").Append(CatchupUtil.F(_bonus.Value))
                  .Append(" max=x").Append(CatchupUtil.F(_maxFactor.Value));
                var p = Player.m_localPlayer;
                if (p != null && _ceiling.Count > 0)
                {
                    var skills = p.GetSkills();
                    string worst = null;
                    float worstFactor = 1f;
                    foreach (var kv in _ceiling)
                    {
                        float lvl = skills.GetSkillLevel((Skills.SkillType)kv.Key);
                        float f = MultiplierFor(lvl, kv.Value);
                        if (f > worstFactor) { worstFactor = f; worst = ((Skills.SkillType)kv.Key).ToString(); }
                    }
                    if (worst != null)
                        sb.Append(" biggest gap: ").Append(worst).Append(" x").Append(CatchupUtil.F(worstFactor));
                }
            }
            return sb.ToString();
        }

        // ---- RPC registration ----------------------------------------------------------------

        private static void ZNetAwakePostfix()
        {
            if (_self == null || !_self.Active) return;
            try
            {
                var rrpc = ZRoutedRpc.instance;
                if (rrpc == null || ReferenceEquals(rrpc, _rpcRegisteredOn)) return;
                rrpc.Register<ZPackage>(RpcReport, RPC_SkillReport);
                rrpc.Register<ZPackage>(RpcCeiling, RPC_SkillCeiling);
                _rpcRegisteredOn = rrpc;
                _self._reports.Clear();
                _self._serverCeiling.Clear();
                _ceiling.Clear();
                Log.LogInfo("[SkillCatchup] routed RPCs '" + RpcReport + "' / '" + RpcCeiling + "' registered");
            }
            catch (Exception e) { Log.LogError("[SkillCatchup] RPC register failed: " + e); }
        }

        // ---- the tick ----------------------------------------------------------------------------

        private static void ZNetUpdatePostfix()
        {
            if (_self == null || !_self.Active) return;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (ServerActive() && now >= _self._nextBroadcast)
                {
                    _self._nextBroadcast = now + Mathf.Max(10, _self._broadcastSec.Value);
                    _self.RunSelfTestOnce();
                    _self.RecomputeCeiling(true);
                }
                if (ClientActive() && now >= _self._nextReport)
                {
                    _self._nextReport = now + Mathf.Max(10, _self._reportSec.Value);
                    _self.SendReport();
                }
            }
            catch (Exception e) { Log.LogError("[SkillCatchup] tick failed: " + e); }
        }

        // ---- client -> server ----------------------------------------------------------------------

        private void SendReport()
        {
            var p = Player.m_localPlayer;
            if (p == null) return;
            var skills = p.GetSkills();
            if (skills == null) return;
            var rrpc = ZRoutedRpc.instance;
            if (rrpc == null) return;

            var list = skills.GetSkillList();
            var pkg = new ZPackage();
            int count = 0;
            foreach (var s in list) if (s != null && s.m_info != null && s.m_level > 0f) count++;
            pkg.Write(count);
            foreach (var s in list)
            {
                if (s == null || s.m_info == null || s.m_level <= 0f) continue;
                pkg.Write((int)s.m_info.m_skill);
                pkg.Write(s.m_level);
            }
            rrpc.InvokeRoutedRPC(RpcReport, pkg);
        }

        /// <summary>SERVER: a client sent its skill levels.</summary>
        private static void RPC_SkillReport(long sender, ZPackage pkg)
        {
            if (_self == null || !_self.Active || !ServerActive() || pkg == null) return;
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return;
                var peer = znet.GetPeer(sender);
                string key, name;
                if (peer != null)
                {
                    key = CatchupUtil.PeerKey(peer);
                    name = peer.m_playerName;
                }
                else
                {
                    // The host's own player on a listen server has no peer entry.
                    var lp = Player.m_localPlayer;
                    name = lp != null ? lp.GetPlayerName() : "host";
                    key = "local|" + name;
                }
                if (string.IsNullOrEmpty(key)) return;

                pkg.SetPos(0);
                int n = pkg.ReadInt();
                if (n < 0 || n > 256) return;
                var rep = new Report { Key = key, Name = name, When = CatchupUtil.NowUnix() };
                for (int i = 0; i < n; i++)
                {
                    int type = pkg.ReadInt();
                    float level = pkg.ReadSingle();
                    if (float.IsNaN(level) || level <= 0f || level > 100f) continue;
                    rep.Levels[type] = level;
                }
                _self._reports[key] = rep;
                _self.RecomputeCeiling(false);
            }
            catch (Exception e) { Log.LogWarning("[SkillCatchup] bad skill report from " + sender + ": " + e.Message); }
        }

        // ---- server: the ceiling ----------------------------------------------------------------------

        private void RecomputeCeiling(bool forceBroadcast)
        {
            long cutoff = CatchupUtil.NowUnix() - (long)Mathf.Max(1, _windowDays.Value) * 86400L;
            var fresh = new Dictionary<int, float>();
            int used = 0;
            foreach (var rep in _reports.Values)
            {
                if (rep.When < cutoff) continue;
                used++;
                foreach (var kv in rep.Levels)
                {
                    float cur;
                    if (!fresh.TryGetValue(kv.Key, out cur) || kv.Value > cur) fresh[kv.Key] = kv.Value;
                }
            }

            bool changed = fresh.Count != _serverCeiling.Count;
            if (!changed)
                foreach (var kv in fresh)
                {
                    float old;
                    if (!_serverCeiling.TryGetValue(kv.Key, out old) || Mathf.Abs(old - kv.Value) > 0.01f)
                    { changed = true; break; }
                }

            if (changed)
            {
                _serverCeiling.Clear();
                foreach (var kv in fresh) _serverCeiling[kv.Key] = kv.Value;
            }
            if (!changed && !forceBroadcast) return;

            string msg = "[SkillCatchup] ceiling over " + used + " report(s) in the last " +
                         _windowDays.Value + "d: " + Describe(_serverCeiling);
            if (!_loggedOnce) { Log.LogInfo(msg); _loggedOnce = true; }
            else Log.LogDebug(msg);

            Broadcast();
        }

        private void Broadcast()
        {
            var rrpc = ZRoutedRpc.instance;
            if (rrpc == null) return;
            var pkg = new ZPackage();
            pkg.Write(_serverCeiling.Count);
            foreach (var kv in _serverCeiling)
            {
                pkg.Write(kv.Key);
                pkg.Write(kv.Value);
            }
            rrpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcCeiling, pkg);
        }

        private static string Describe(Dictionary<int, float> d)
        {
            if (d.Count == 0) return "(empty)";
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append((Skills.SkillType)kv.Key).Append('=').Append(CatchupUtil.F(kv.Value));
            }
            return sb.ToString();
        }

        private static void DisconnectPrefix(ZNetPeer peer)
        {
            // Nothing to clean up server-side (reports age out of the window on their own), but a
            // client that leaves must forget the ceiling so a single-player world is untouched.
            if (_self == null || !_self.Active) return;
            if (ServerActive()) return;
            _ceiling.Clear();
        }

        // ---- server -> client ------------------------------------------------------------------------

        /// <summary>CLIENT: the server broadcast a new group ceiling.</summary>
        private static void RPC_SkillCeiling(long sender, ZPackage pkg)
        {
            if (_self == null || !_self.Active || !ClientActive() || pkg == null) return;
            try
            {
                pkg.SetPos(0);
                int n = pkg.ReadInt();
                if (n < 0 || n > 256) return;
                _ceiling.Clear();
                for (int i = 0; i < n; i++)
                {
                    int type = pkg.ReadInt();
                    float level = pkg.ReadSingle();
                    if (float.IsNaN(level) || level <= 0f) continue;
                    _ceiling[type] = Mathf.Clamp(level, 0f, 100f);
                }
                Log.LogInfo("[SkillCatchup] group ceiling: " + Describe(_ceiling));
            }
            catch (Exception e) { Log.LogWarning("[SkillCatchup] bad ceiling packet: " + e.Message); }
        }

        // ---- client: apply ------------------------------------------------------------------------------

        /// <summary>min(MaxFactor, 1 + Bonus * (ceiling - level) / ceiling), never below 1.</summary>
        private static float MultiplierFor(float level, float ceiling)
        {
            if (_self == null) return 1f;
            if (ceiling <= 0f || level >= ceiling) return 1f;
            float f = 1f + Mathf.Max(0f, _self._bonus.Value) * ((ceiling - level) / ceiling);
            float cap = Mathf.Max(1f, _self._maxFactor.Value);
            return Mathf.Clamp(f, 1f, cap);
        }

        private static void RaiseSkillPrefix(Skills __instance, Skills.SkillType skillType, ref float factor)
        {
            if (_self == null || !_self.Active || !ClientActive()) return;
            if (skillType == Skills.SkillType.None || _ceiling.Count == 0) return;
            var p = Player.m_localPlayer;
            if (p == null || __instance == null || !ReferenceEquals(__instance.m_player, p)) return;
            float ceiling;
            if (!_ceiling.TryGetValue((int)skillType, out ceiling)) return;
            float level = __instance.GetSkillLevel(skillType);
            float mult = MultiplierFor(level, ceiling);
            if (mult > 1.0001f) factor *= mult;
        }

        // ---- self test -------------------------------------------------------------------------------------

        /// <summary>
        /// [Catchup] SelfTest = true: seed three fake reports and log the ceiling they produce plus
        /// the multiplier a player 20 levels behind would get. In-memory only, nothing is written.
        /// </summary>
        private void RunSelfTestOnce()
        {
            if (_selfTestDone || !CatchupUtil.SelfTest) return;
            _selfTestDone = true;
            try
            {
                long now = CatchupUtil.NowUnix();
                _reports.Clear();
                AddFake("Alfr", now, 60f, 45f, 30f);
                AddFake("Bjorn", now, 40f, 55f, 20f);
                AddFake("Dagny", now, 12f, 8f, 35f);

                RecomputeCeiling(true);
                Log.LogInfo("[Catchup SelfTest] skills: " + _reports.Count + " seeded reports, ceiling = " +
                            Describe(_serverCeiling) + " (Bonus=" + CatchupUtil.F(_bonus.Value) +
                            " MaxFactor=" + CatchupUtil.F(_maxFactor.Value) + ")");
                foreach (var kv in _serverCeiling)
                {
                    var t = (Skills.SkillType)kv.Key;
                    Log.LogInfo("[Catchup SelfTest]   " + t + ": ceiling " + CatchupUtil.F(kv.Value) +
                                " -> Dagny(" + CatchupUtil.F(_reports["fake|Dagny"].Levels[kv.Key]) + ") x" +
                                CatchupUtil.F(MultiplierFor(_reports["fake|Dagny"].Levels[kv.Key], kv.Value)) +
                                ", Alfr(" + CatchupUtil.F(_reports["fake|Alfr"].Levels[kv.Key]) + ") x" +
                                CatchupUtil.F(MultiplierFor(_reports["fake|Alfr"].Levels[kv.Key], kv.Value)));
                }
                _reports.Clear();
                _serverCeiling.Clear();
                Log.LogInfo("[Catchup SelfTest] skill self test done, fake reports discarded");
            }
            catch (Exception e) { Log.LogError("[Catchup SelfTest] skill self test failed: " + e); }
        }

        private void AddFake(string name, long when, float axes, float blocking, float woodcutting)
        {
            var rep = new Report { Key = "fake|" + name, Name = name, When = when };
            rep.Levels[(int)Skills.SkillType.Axes] = axes;
            rep.Levels[(int)Skills.SkillType.Blocking] = blocking;
            rep.Levels[(int)Skills.SkillType.WoodCutting] = woodcutting;
            _reports[rep.Key] = rep;
        }
    }
}
