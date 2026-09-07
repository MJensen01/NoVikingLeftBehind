using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// OreRegrowth (SERVER ONLY) - mined-out ore nodes of a tier that is behind the frontier
    /// come back after N in-game days.
    ///
    /// DESTROY FUNNEL (verified in the 0.221.12 decompile of ZDOMan):
    ///   The owning client mines the last hit area of a MineRock5 -> MineRock5.DamageArea sees
    ///   AllDestroyed() -> m_nview.Destroy() -> ZNetScene.Destroy -> ZDOMan.DestroyZDO(zdo),
    ///   which only appends to m_destroySendList. SendDestroyed() then packs the list and calls
    ///   ZRoutedRpc.InvokeRoutedRPC(Everybody, "DestroyZDO", pkg). Everybody includes the server,
    ///   so the server's ZDOMan.RPC_DestroyZDO(long, ZPackage) runs and calls
    ///   ZDOMan.HandleDestroyedZDO(ZDOID) once per uid.
    ///
    ///   HandleDestroyedZDO is the SINGLE funnel: the routed-RPC path, the server's own
    ///   DestroyZDO of a resurrected dead ZDO (ZDOMan.cs:855) and everything else converge on it,
    ///   and it is the last place the ZDO still exists (it does GetZDO(uid) itself and returns
    ///   early when the ZDO is already gone). We prefix it, so we can read prefab/pos/rot before
    ///   the ZDO is released to the pool. The early-return on a second delivery of the same uid
    ///   also gives us free de-duplication.
    ///
    ///   ONE EVENT PER NODE, not one per hit: MineRock5 removes fragments per hit area but only
    ///   calls m_nview.Destroy() inside `if (AllDestroyed())` (MineRock5.decompiled.cs:400-403),
    ///   and MineRock likewise at MineRock.decompiled.cs:180-182.
    ///
    ///   SURPRISE, verified live on 0.221.12: of the five default prefabs only
    ///   MineRock_Meteorite is a MineRock/MineRock5. rock4_copper, MineRock_Tin, silvervein and
    ///   MineRock_Obsidian are plain `Destructible` nodes (Destructible + ZNetView + HoverText +
    ///   DropOnDestroyed / TerrainModifier). That changes nothing here: Destructible.Destroy ends
    ///   in ZNetScene.instance.Destroy(gameObject) (Destructible.decompiled.cs:180) - the same
    ///   funnel - and is inherently one event per node. The allowlist keys off the prefab hash,
    ///   not off a component type, so both families work and modded nodes do too.
    ///
    /// RESPAWN:
    ///   A fresh ZDO is built exactly the way ZNetView.Awake builds one for a brand-new object
    ///   (ZNetView.decompiled.cs:84-92): CreateNewZDO(pos, hash), then Persistent / Type /
    ///   Distant copied off the prefab's own ZNetView, then SetPrefab(hash), SetRotation(rot).
    ///   Additionally SetOwner(0) so the first client to load the sector claims it instead of the
    ///   headless server pretending to simulate it. No GameObject is needed on the server: the
    ///   ZDO is persistent, gets saved with the world, and clients instantiate the prefab when
    ///   the sector enters their active area.
    /// </summary>
    internal sealed class OreRegrowthModule : FeatureModule
    {
        public override string Name => "OreRegrowth";
        public override ModuleSide Side => ModuleSide.Server;
        public override string Section => "Regrowth";

        protected override string EnabledDescription =>
            "Regrow mined-out ore nodes whose material tier is behind the frontier. " +
            "Server only: recording and respawning both happen on the dedicated server.";

        // ---- config -------------------------------------------------------------------

        private ConfigEntry<string> _prefabs;
        private ConfigEntry<int> _regrowDays;
        private ConfigEntry<float> _checkIntervalSec;
        private ConfigEntry<float> _minPlayerDistance;
        private ConfigEntry<int> _maxPerTick;
        private ConfigEntry<bool> _dryRun;
        private ConfigEntry<bool> _selfTest;

        public const string DefaultPrefabs =
            "rock4_copper:1,MineRock_Tin:1,silvervein:3,MineRock_Obsidian:3,MineRock_Meteorite:4";

        // ---- state --------------------------------------------------------------------

        internal static OreRegrowthModule Instance;

        /// <summary>prefab name hash -> material tier. Rebuilt from config, resolved against ZNetScene.</summary>
        private readonly Dictionary<int, RegrowthPrefab> _allow = new Dictionary<int, RegrowthPrefab>();
        private bool _allowResolved;
        private string _allowSummary = "(not resolved yet)";

        private readonly List<RegrowthEntry> _pending = new List<RegrowthEntry>();
        private bool _loaded;
        private bool _dirty;
        private GameObject _tickerGo;
        private bool _selfTestDone;

        internal float CheckIntervalSec => _checkIntervalSec == null ? 60f : Mathf.Max(1f, _checkIntervalSec.Value);
        internal bool SelfTestWanted => _selfTest != null && _selfTest.Value && !_selfTestDone;

        internal string StorePath
        {
            get { return Path.Combine(Path.Combine(Paths.ConfigPath, "nvlb"), "regrowth.json"); }
        }

        // ---- lifecycle ----------------------------------------------------------------

        protected override void Bind()
        {
            _prefabs = BindSynced("Prefabs", DefaultPrefabs,
                "Ore node prefabs that regrow, as name:tier,name:tier. The tier is the material " +
                "tier used against the frontier (see [Tiers]/[Frontier]). Names are resolved " +
                "against ZNetScene's prefab list at runtime; unknown names are logged and ignored.");

            _regrowDays = BindSynced("RegrowDays", 7,
                "In-game days a mined-out node stays gone before it may regrow.");

            _checkIntervalSec = BindSynced("CheckIntervalSec", 60f,
                "Real seconds between respawn sweeps on the server.");

            _minPlayerDistance = BindSynced("MinPlayerDistance", 64f,
                "Never respawn a node with a player this close (metres) - nobody sees ore pop in.");

            _maxPerTick = BindSynced("MaxPerTick", 5,
                "Maximum nodes respawned per sweep, so a long backlog trickles back in.");

            _dryRun = BindLocal("DryRun", false,
                "Log what would be respawned without creating any ZDO. Machine-local.");

            _selfTest = BindLocal("SelfTest", false,
                "Headless proof: pick an existing copper node, fake a due destroy record for it, " +
                "run one sweep and verify a new ZDO appeared. Machine-local, runs once per boot.");
        }

        protected override void ApplyPatches()
        {
            var target = AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });
            if (target == null)
                throw new Exception("NoVikingLeftBehind OreRegrowth: ZDOMan.HandleDestroyedZDO(ZDOID) not found");

            var prefix = AccessTools.Method(typeof(OreRegrowthModule), nameof(HandleDestroyedZDO_Prefix));
            if (prefix == null)
                throw new Exception("NoVikingLeftBehind OreRegrowth: own prefix method not found");

            Harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Instance = this;

            LoadStore();
            StartTicker();

            Log.LogInfo("[OreRegrowth] hooked ZDOMan.HandleDestroyedZDO; store=" + StorePath +
                        " pending=" + _pending.Count +
                        " regrowDays=" + _regrowDays.Value +
                        " interval=" + CheckIntervalSec.ToString("0.#") + "s" +
                        " minDist=" + _minPlayerDistance.Value.ToString("0.#") + "m" +
                        " maxPerTick=" + _maxPerTick.Value +
                        " dryRun=" + _dryRun.Value +
                        " selfTest=" + _selfTest.Value);
        }

        public override void Disable()
        {
            StopTicker();
            base.Disable();
            if (Instance == this) Instance = null;
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != null && entry.Definition.Key == "Prefabs")
            {
                _allowResolved = false;
                _allow.Clear();
                Log.LogInfo("[OreRegrowth] Prefabs changed, allowlist will be re-resolved on the next sweep");
            }
        }

        public override string StatusDetail()
        {
            if (_regrowDays == null) return null;
            return "pending=" + _pending.Count +
                   " regrowDays=" + _regrowDays.Value +
                   " interval=" + CheckIntervalSec.ToString("0.#") + "s" +
                   " minDist=" + _minPlayerDistance.Value.ToString("0.#") + "m" +
                   " allow=" + _allowSummary +
                   (_dryRun.Value ? " DRYRUN" : "");
        }

        // ---- ticker -------------------------------------------------------------------

        private void StartTicker()
        {
            if (_tickerGo != null) return;
            _tickerGo = new GameObject("NVLB_RegrowthTicker");
            UnityEngine.Object.DontDestroyOnLoad(_tickerGo);
            _tickerGo.hideFlags = HideFlags.HideAndDontSave;
            _tickerGo.AddComponent<RegrowthTicker>();
        }

        private void StopTicker()
        {
            if (_tickerGo == null) return;
            try { UnityEngine.Object.Destroy(_tickerGo); } catch { }
            _tickerGo = null;
        }

        // ---- destroy hook -------------------------------------------------------------

        private static void HandleDestroyedZDO_Prefix(ZDOMan __instance, ZDOID uid)
        {
            var self = Instance;
            if (self == null || !self.Active) return;
            if (!ServerActive()) return;

            try
            {
                var zdo = __instance.GetZDO(uid);
                if (zdo == null) return;               // already handled; free de-duplication

                self.EnsureAllowlist();
                RegrowthPrefab info;
                if (!self._allow.TryGetValue(zdo.GetPrefab(), out info)) return;

                var e = new RegrowthEntry
                {
                    prefabHash = info.Hash,
                    name = info.Name,
                    tier = info.Tier,
                    day = CurrentDay(),
                    x = zdo.GetPosition().x,
                    y = zdo.GetPosition().y,
                    z = zdo.GetPosition().z
                };
                var euler = zdo.GetRotation().eulerAngles;
                e.rx = euler.x; e.ry = euler.y; e.rz = euler.z;

                self._pending.Add(e);
                self._dirty = true;
                self.SaveStore();

                Log.LogInfo("[OreRegrowth] recorded destroyed " + info.Name + " tier=" + info.Tier +
                            " at " + Fmt(e.Pos) + " day=" + e.day + " (pending=" + self._pending.Count + ")");
            }
            catch (Exception ex)
            {
                Log.LogError("[OreRegrowth] destroy hook failed: " + ex.Message);
            }
        }

        // ---- allowlist ----------------------------------------------------------------

        internal void EnsureAllowlist()
        {
            if (_allowResolved) return;
            if (ZNetScene.instance == null) return;   // not ready yet; try again next tick

            _allow.Clear();
            var resolved = new List<string>();
            var missing = new List<string>();
            var noMineRock = new List<string>();

            var spec = _prefabs == null ? DefaultPrefabs : _prefabs.Value;
            foreach (var raw in spec.Split(','))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;

                var parts = s.Split(':');
                var name = parts[0].Trim();
                if (name.Length == 0) continue;

                int tier = 1;
                if (parts.Length > 1 && !int.TryParse(parts[1].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out tier))
                {
                    Log.LogWarning("[OreRegrowth] bad tier in Prefabs entry '" + s + "', using 1");
                    tier = 1;
                }

                int hash = name.GetStableHashCode();
                var go = ZNetScene.instance.GetPrefab(hash);
                if (go == null) { missing.Add(name); continue; }

                // MineRock5 is usually on a child of the prefab root, not the root itself
                // (the root carries the ZNetView), so search the whole hierarchy incl. inactive.
                bool isMineRock = go.GetComponentInChildren<MineRock5>(true) != null ||
                                  go.GetComponentInChildren<MineRock>(true) != null;
                if (!isMineRock)
                {
                    var comps = go.GetComponentsInChildren<MonoBehaviour>(true);
                    var names = new List<string>();
                    for (int c = 0; c < comps.Length && names.Count < 12; c++)
                        if (comps[c] != null) names.Add(comps[c].GetType().Name);
                    noMineRock.Add(name + "[" + string.Join("+", names.ToArray()) +
                                   (comps.Length > names.Count ? "+..." : "") + "]");
                }

                _allow[hash] = new RegrowthPrefab { Hash = hash, Name = name, Tier = tier };
                resolved.Add(name + ":" + tier + "(" + hash + (isMineRock ? "" : ",noMineRock") + ")");
            }

            _allowResolved = true;
            _allowSummary = _allow.Count + "/" + (resolved.Count + missing.Count);

            Log.LogInfo("[OreRegrowth] prefab allowlist resolved: " +
                        (resolved.Count == 0 ? "(none)" : string.Join(", ", resolved.ToArray())));
            if (missing.Count > 0)
                Log.LogWarning("[OreRegrowth] prefab names NOT found in ZNetScene (ignored): " +
                               string.Join(", ", missing.ToArray()));
            if (noMineRock.Count > 0)
                // Expected for copper/tin/silver/obsidian: on 0.221.12 those are plain
                // Destructible nodes, not MineRock5. Informational, never a failure - we key
                // off the prefab hash and both component families destroy the whole ZDO the
                // same way (ZNetScene.Destroy -> ZDOMan.DestroyZDO -> HandleDestroyedZDO).
                Log.LogInfo("[OreRegrowth] allowlisted prefabs that are Destructible rather than " +
                            "MineRock/MineRock5 (fine, same destroy funnel): " +
                            string.Join(", ", noMineRock.ToArray()));
        }

        // ---- the sweep ----------------------------------------------------------------

        /// <summary>One respawn sweep. Returns how many nodes were respawned.</summary>
        internal int RunSweep()
        {
            if (!Active || !ServerActive()) return 0;
            EnsureAllowlist();
            if (_pending.Count == 0) return 0;

            int today = CurrentDay();
            int budget = Mathf.Max(0, _maxPerTick.Value);
            int done = 0;

            for (int i = _pending.Count - 1; i >= 0 && done < budget; i--)
            {
                var e = _pending[i];

                if (today - e.day < _regrowDays.Value) continue;
                if (!Tiers.IsBehind(e.tier)) continue;
                if (PlayerWithin(e.Pos, _minPlayerDistance.Value)) continue;

                if (_dryRun.Value)
                {
                    Log.LogInfo("[OreRegrowth] DRYRUN would respawn " + e.name + " at " + Fmt(e.Pos) +
                                " after " + (today - e.day) + " days");
                    continue;
                }

                ZDO zdo;
                try { zdo = Respawn(e); }
                catch (Exception ex)
                {
                    Log.LogError("[OreRegrowth] respawn of " + e.name + " at " + Fmt(e.Pos) + " failed: " + ex.Message);
                    continue;
                }
                if (zdo == null) continue;

                _pending.RemoveAt(i);
                _dirty = true;
                done++;

                Log.LogInfo("[OreRegrowth] respawned " + e.name + " at " + Fmt(e.Pos) +
                            " after " + (today - e.day) + " days (zdo=" + zdo.m_uid + ")");
            }

            if (_dirty) SaveStore();
            return done;
        }

        /// <summary>Create the ZDO exactly the way ZNetView.Awake does for a brand-new object.</summary>
        private static ZDO Respawn(RegrowthEntry e)
        {
            if (ZNetScene.instance == null || ZDOMan.instance == null) return null;

            var prefab = ZNetScene.instance.GetPrefab(e.prefabHash);
            if (prefab == null)
                throw new Exception("prefab hash " + e.prefabHash + " (" + e.name + ") not in ZNetScene");

            var nv = prefab.GetComponent<ZNetView>();
            if (nv == null)
                throw new Exception("prefab " + e.name + " has no ZNetView");

            var zdo = ZDOMan.instance.CreateNewZDO(e.Pos, e.prefabHash);
            // Same order as ZNetView.Awake (ZNetView.decompiled.cs:85-91).
            zdo.Persistent = nv.m_persistent;
            zdo.Type = nv.m_type;
            zdo.Distant = nv.m_distant;
            zdo.SetPrefab(e.prefabHash);
            zdo.SetRotation(e.Rot);
            // Hand it to nobody: the first client whose active area covers the sector claims it.
            zdo.SetOwner(0L);
            return zdo;
        }

        // ---- helpers ------------------------------------------------------------------

        internal static int CurrentDay()
        {
            if (EnvMan.instance != null) return EnvMan.instance.GetDay();
            // Fallback: 1 in-game day == 1800 s of world time (EnvMan.m_dayLengthSec default).
            if (ZNet.instance != null) return (int)(ZNet.instance.GetTimeSeconds() / 1800.0);
            return 0;
        }

        internal static string DaySource()
        {
            return EnvMan.instance != null ? "EnvMan.GetDay" : "ZNet.GetTimeSeconds/1800";
        }

        private static bool PlayerWithin(Vector3 pos, float dist)
        {
            var znet = ZNet.instance;
            if (znet == null) return true;             // no idea where anyone is -> do not spawn
            float sq = dist * dist;

            var peers = znet.GetPeers();
            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    var p = peers[i];
                    if (p == null) continue;
                    if ((p.m_refPos - pos).sqrMagnitude <= sq) return true;
                }
            }

            // A listen-server host is not in GetPeers().
            if (!znet.IsDedicated() && (znet.GetReferencePosition() - pos).sqrMagnitude <= sq) return true;
            return false;
        }

        internal static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                   v.y.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                   v.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        // ---- persistence ---------------------------------------------------------------

        private void LoadStore()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = StorePath;
                if (!File.Exists(path))
                {
                    Log.LogInfo("[OreRegrowth] no store at " + path + ", starting empty");
                    return;
                }
                var json = File.ReadAllText(path);
                var loaded = RegrowthJson.Read(json);
                _pending.AddRange(loaded);
                Log.LogInfo("[OreRegrowth] loaded " + _pending.Count + " pending node(s) from " + path);
            }
            catch (Exception e)
            {
                Log.LogError("[OreRegrowth] could not read the store, starting empty: " + e.Message);
                _pending.Clear();
            }
        }

        /// <summary>Atomic-ish write: full file to .tmp, then replace. Never throws.</summary>
        internal void SaveStore()
        {
            if (!_dirty) return;
            try
            {
                var path = StorePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var json = RegrowthJson.Write(_pending);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _dirty = false;
            }
            catch (Exception e)
            {
                Log.LogError("[OreRegrowth] could not write the store: " + e.Message);
            }
        }

        // ---- headless self test ---------------------------------------------------------

        internal void RunSelfTest()
        {
            _selfTestDone = true;
            try
            {
                EnsureAllowlist();
                if (ZDOMan.instance == null || ZNetScene.instance == null)
                {
                    Log.LogWarning("[OreRegrowth][SelfTest] ZDOMan/ZNetScene not ready, skipped");
                    return;
                }

                const string probe = "rock4_copper";
                int hash = probe.GetStableHashCode();
                RegrowthPrefab info;
                if (!_allow.TryGetValue(hash, out info))
                {
                    Log.LogWarning("[OreRegrowth][SelfTest] " + probe + " is not in the allowlist, skipped");
                    return;
                }

                // (1) find an existing copper node in the loaded world.
                ZDO found = null;
                foreach (var kv in ZDOMan.instance.m_objectsByID)
                {
                    if (kv.Value != null && kv.Value.GetPrefab() == hash) { found = kv.Value; break; }
                }
                if (found == null)
                {
                    Log.LogWarning("[OreRegrowth][SelfTest] no existing " + probe +
                                   " ZDO in the world (" + ZDOMan.instance.m_objectsByID.Count + " ZDOs), skipped");
                    return;
                }
                var pos = found.GetPosition();
                var oldId = found.m_uid;
                Log.LogInfo("[OreRegrowth][SelfTest] step 1: found existing " + probe +
                            " zdo=" + oldId + " at " + Fmt(pos));

                // (2) fake a long-overdue destroy record for it.
                var euler = found.GetRotation().eulerAngles;
                var e = new RegrowthEntry
                {
                    prefabHash = hash, name = probe, tier = info.Tier, day = -999,
                    x = pos.x, y = pos.y, z = pos.z, rx = euler.x, ry = euler.y, rz = euler.z
                };
                _pending.Add(e);
                _dirty = true;
                Log.LogInfo("[OreRegrowth][SelfTest] step 2: recorded fake entry tier=" + e.tier +
                            " day=" + e.day + " today=" + CurrentDay() + " (" + DaySource() + ")" +
                            " frontier: " + Frontier.Describe() + " IsBehind(" + e.tier + ")=" +
                            Tiers.IsBehind(e.tier));

                if (!Tiers.IsBehind(e.tier))
                    Log.LogWarning("[OreRegrowth][SelfTest] tier " + e.tier + " is NOT behind the " +
                                   "frontier right now, so the sweep will (correctly) refuse. Set " +
                                   "[Frontier] TierOverride >= " + (e.tier + 1) + " to exercise the respawn.");

                // Snapshot every ZDO of this prefab already sitting at that spot, so step 4 can
                // only ever find something the sweep itself created.
                var before4 = new HashSet<ZDOID>();
                foreach (var kv in ZDOMan.instance.m_objectsByID)
                {
                    var z = kv.Value;
                    if (z != null && z.GetPrefab() == hash && (z.GetPosition() - pos).sqrMagnitude <= 1f)
                        before4.Add(z.m_uid);
                }

                // (3) one sweep.
                int n = RunSweep();
                Log.LogInfo("[OreRegrowth][SelfTest] step 3: sweep respawned " + n + " node(s)");

                // (4) confirm a NEW ZDO of that prefab exists near the position.
                ZDO fresh = null;
                foreach (var kv in ZDOMan.instance.m_objectsByID)
                {
                    var z = kv.Value;
                    if (z == null || z.GetPrefab() != hash) continue;
                    if (before4.Contains(z.m_uid)) continue;   // was already there before the sweep
                    if ((z.GetPosition() - pos).sqrMagnitude > 1f) continue;
                    fresh = z; break;
                }
                if (fresh == null)
                {
                    Log.LogError("[OreRegrowth][SelfTest] step 4: FAIL - the sweep created no new " +
                                 probe + " ZDO within 1 m of " + Fmt(pos) +
                                 " (sweep respawned " + n + "; IsBehind(" + e.tier + ")=" +
                                 Tiers.IsBehind(e.tier) + ")");
                }
                else
                {
                    var back = ZDOMan.instance.GetZDO(fresh.m_uid);
                    Log.LogInfo("[OreRegrowth][SelfTest] step 4: PASS - new zdo=" + fresh.m_uid +
                                " prefab=" + fresh.GetPrefab() + " at " + Fmt(fresh.GetPosition()) +
                                " persistent=" + fresh.Persistent + " distant=" + fresh.Distant +
                                " type=" + fresh.Type + " owner=" + fresh.GetOwner() +
                                " GetZDO(round-trip)=" + (back != null));
                }

                // (5) exercise the RECORDING half through the real funnel, on the node we just
                // made - which also removes the duplicate we added to the world.
                if (fresh != null)
                {
                    int before = _pending.Count;
                    var freshId = fresh.m_uid;          // ZDOPool.Release resets m_uid, capture first
                    ZDOMan.instance.HandleDestroyedZDO(freshId);
                    bool recorded = _pending.Count == before + 1;
                    Log.LogInfo("[OreRegrowth][SelfTest] step 5: HandleDestroyedZDO(" + freshId + ") -> " +
                                (recorded ? "RECORDED" : "NOT RECORDED") +
                                ", zdo gone=" + (ZDOMan.instance.GetZDO(freshId) == null));
                    if (recorded) _pending.RemoveAt(_pending.Count - 1);
                }

                // Leave nothing behind in the store.
                _pending.Remove(e);
                _dirty = true;
                SaveStore();
            }
            catch (Exception ex)
            {
                Log.LogError("[OreRegrowth][SelfTest] threw: " + ex);
            }
        }
    }

    /// <summary>One allowlisted ore prefab.</summary>
    internal sealed class RegrowthPrefab
    {
        public int Hash;
        public string Name;
        public int Tier;
    }

    /// <summary>One mined-out node waiting to come back. Unity-JsonUtility serialisable (flat fields).</summary>
    [Serializable]
    internal sealed class RegrowthEntry
    {
        public int prefabHash;
        public string name;
        public int tier;
        public int day;
        public float x, y, z;
        public float rx, ry, rz;

        public Vector3 Pos { get { return new Vector3(x, y, z); } }
        public Quaternion Rot { get { return Quaternion.Euler(rx, ry, rz); } }
    }
}
