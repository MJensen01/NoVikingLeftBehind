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
        public override string Theme => "Catching up";
        public override string Hint => "Mined ore nodes come back after a while";

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

        /// <summary>
        /// `recordPrefab:tier[:respawnPrefab]`.
        ///
        /// FRACTURED STAGES. A copper vein is TWO prefabs: `rock4_copper` (a Destructible) is
        /// swapped for `rock4_copper_frac` (the MineRock5 you actually mine) on the FIRST pickaxe
        /// hit, via Destructible.m_spawnWhenDestroyed (Destructible.cs:166-168). Recording on the
        /// Destructible's destroy - what 0.4.4 did - therefore fired one hit into the vein, while
        /// the fractured copy was still standing and still full of ore: the node "regrew" beside
        /// its own live remains and the ore could be taken twice. So the RECORD prefab is the
        /// `_frac` stage (its ZDO dies only when every hit area is mined out) and the RESPAWN
        /// prefab is the original vein. Ores with no fractured stage (tin, obsidian, meteorite)
        /// keep the 0.4.4 behaviour: one name, recorded and respawned as itself.
        /// </summary>
        public const string DefaultPrefabs =
            "rock4_copper_frac:1:rock4_copper,silvervein_frac:3:silvervein," +
            "MineRock_Tin:1,MineRock_Obsidian:3,MineRock_Meteorite:4";

        /// <summary>Metres: a node is not respawned if one is already standing this close.</summary>
        private const float DedupeRadius = 4f;

        // ---- state --------------------------------------------------------------------

        internal static OreRegrowthModule Instance;

        /// <summary>RECORD prefab hash -> entry. Rebuilt from config, resolved against ZNetScene.</summary>
        private readonly Dictionary<int, RegrowthPrefab> _allow = new Dictionary<int, RegrowthPrefab>();
        /// <summary>respawn prefab hash -> its fractured stage's hash, for the dedupe guard.</summary>
        private readonly Dictionary<int, int> _fracOf = new Dictionary<int, int>();
        private bool _allowResolved;
        private bool _fracLogged;
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
                "Ore nodes that regrow, as recordPrefab:tier[:respawnPrefab], comma separated. " +
                "recordPrefab is the prefab whose destruction means 'this node is mined out' - for " +
                "copper and silver that is the FRACTURED stage (rock4_copper_frac / silvervein_frac), " +
                "because the un-fractured vein is destroyed on the very first pickaxe hit. " +
                "respawnPrefab is what comes back; omit it and the recorded prefab comes back as " +
                "itself (correct for tin/obsidian/meteorite, which have no fractured stage). The " +
                "tier is the material tier used against the frontier (see [Tiers]/[Frontier]). " +
                "The old two-part form name:tier is still accepted and is migrated automatically " +
                "when a <name>_frac prefab exists. Names are resolved against ZNetScene's prefab " +
                "list at runtime; unknown names are logged and ignored.",
                Opt.T("Which ore nodes regrow and what they turn into")
                    .Pick(new PickerSpec(PickerSource.OreNodes,
                        new PickerField("Tier", 0, 7, 1, true))
                        .Optional().ThenPrefab("Respawns as")));

            _regrowDays = BindSynced("RegrowDays", 7,
                "In-game days a mined-out node stays gone before it may regrow.",
                Opt.N("Days before a mined ore node comes back", 0, 60));

            _checkIntervalSec = BindSynced("CheckIntervalSec", 60f,
                "Real seconds between respawn sweeps on the server.",
                Opt.N("How often the server checks for nodes to respawn", 5, 600));

            _minPlayerDistance = BindSynced("MinPlayerDistance", 64f,
                "Never respawn a node with a player this close (metres) - nobody sees ore pop in.",
                Opt.N("Minimum distance from a player before a node can respawn", 0, 256));

            _maxPerTick = BindSynced("MaxPerTick", 5,
                "Maximum nodes respawned per sweep, so a long backlog trickles back in.",
                Opt.N("Maximum nodes respawned in one check", 1, 50));

            _dryRun = BindLocal("DryRun", false,
                "Log what would be respawned without creating any ZDO. Machine-local.",
                Opt.B("Log what would respawn without actually doing it").Admin());

            _selfTest = BindLocal("SelfTest", false,
                "Headless proof: pick an existing copper node, fake a due destroy record for it, " +
                "run one sweep and verify a new ZDO appeared. Machine-local, runs once per boot.",
                Opt.B("Run a one-time headless test of ore regrowth").Admin());
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

                // The entry stores the RESPAWN prefab, not the recorded one - so regrowth.json is
                // unchanged in shape and every pre-0.4.5 pending record still respawns correctly.
                var e = new RegrowthEntry
                {
                    prefabHash = info.RespawnHash,
                    name = info.RespawnName,
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

                Log.LogInfo("[OreRegrowth] recorded destroyed " + info.Name +
                            (info.RespawnName == info.Name ? "" : " -> will respawn " + info.RespawnName) +
                            " tier=" + info.Tier + " at " + Fmt(e.Pos) + " day=" + e.day +
                            " (pending=" + self._pending.Count + ")");
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
            _fracOf.Clear();
            var resolved = new List<string>();
            var missing = new List<string>();
            var migrated = new List<string>();

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

                // Third part = what comes back. Absent -> the recorded prefab respawns as itself,
                // unless this is a pre-0.4.5 two-part entry that we can migrate (below).
                string respawn = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : name;

                if (parts.Length <= 2)
                {
                    // MIGRATION of the old `name:tier` form, transparent and log-once:
                    //   rock4_copper:1       -> record rock4_copper_frac, respawn rock4_copper
                    //   rock4_copper_frac:1  -> record rock4_copper_frac, respawn rock4_copper
                    // Anything with no fractured stage (MineRock_Tin...) is left exactly as it was.
                    if (!name.EndsWith("_frac", StringComparison.Ordinal) &&
                        ZNetScene.instance.GetPrefab((name + "_frac").GetStableHashCode()) != null)
                    {
                        migrated.Add(name + ":" + tier + " -> " + name + "_frac:" + tier + ":" + name);
                        respawn = name;
                        name = name + "_frac";
                    }
                    else if (name.EndsWith("_frac", StringComparison.Ordinal))
                    {
                        var b = name.Substring(0, name.Length - 5);
                        if (ZNetScene.instance.GetPrefab(b.GetStableHashCode()) != null)
                        {
                            migrated.Add(name + ":" + tier + " -> " + name + ":" + tier + ":" + b);
                            respawn = b;
                        }
                    }
                }

                int hash = name.GetStableHashCode();
                var go = ZNetScene.instance.GetPrefab(hash);
                if (go == null) { missing.Add(name); continue; }

                int respawnHash = respawn.GetStableHashCode();
                if (ZNetScene.instance.GetPrefab(respawnHash) == null)
                {
                    missing.Add(respawn + "(respawn target of " + name + ")");
                    continue;
                }

                _allow[hash] = new RegrowthPrefab
                {
                    Hash = hash, Name = name, Tier = tier,
                    RespawnHash = respawnHash, RespawnName = respawn
                };
                if (respawnHash != hash) _fracOf[respawnHash] = hash;

                resolved.Add(name + ":" + tier + (respawn == name ? "" : ":" + respawn) +
                             "(" + FamilyOf(go) + ")");
            }

            _allowResolved = true;
            _allowSummary = _allow.Count + "/" + (resolved.Count + missing.Count);

            Log.LogInfo("[OreRegrowth] prefab allowlist resolved: " +
                        (resolved.Count == 0 ? "(none)" : string.Join(", ", resolved.ToArray())));
            if (migrated.Count > 0)
                Log.LogInfo("[OreRegrowth] migrated pre-0.4.5 Prefabs entries to the fractured form: " +
                            string.Join(", ", migrated.ToArray()) +
                            " (the config file itself is left alone)");
            if (missing.Count > 0)
                Log.LogWarning("[OreRegrowth] prefab names NOT found in ZNetScene (ignored): " +
                               string.Join(", ", missing.ToArray()));

            LogFracCandidates();
        }

        /// <summary>Component family of a prefab, for the allowlist log. Same shape FastMining uses.</summary>
        private static string FamilyOf(GameObject go)
        {
            // MineRock5 usually sits on a child of the prefab root (the root carries the ZNetView),
            // so search the whole hierarchy incl. inactive children.
            if (go.GetComponentInChildren<MineRock5>(true) != null) return "MineRock5";
            if (go.GetComponentInChildren<MineRock>(true) != null) return "MineRock";
            if (go.GetComponentInChildren<Destructible>(true) != null) return "Destructible";
            return "unknown-family";
        }

        /// <summary>
        /// One-off inventory of every ZNetScene prefab whose name ends in "_frac" and which carries
        /// a MineRock/MineRock5 - i.e. every candidate fractured mining stage the game ships. Logged
        /// once so the ore list can be extended (mistlands/ashlands ores, modded nodes) from evidence
        /// rather than guesswork.
        /// </summary>
        private void LogFracCandidates()
        {
            if (_fracLogged) return;
            _fracLogged = true;
            try
            {
                var names = new List<string>();
                var prefabs = ZNetScene.instance.m_prefabs;
                if (prefabs == null) return;
                for (int i = 0; i < prefabs.Count; i++)
                {
                    var go = prefabs[i];
                    if (go == null || !go.name.EndsWith("_frac", StringComparison.Ordinal)) continue;
                    if (go.GetComponentInChildren<MineRock5>(true) == null &&
                        go.GetComponentInChildren<MineRock>(true) == null) continue;
                    names.Add(go.name + "(" + FamilyOf(go) +
                              (_allow.ContainsKey(go.name.GetStableHashCode()) ? ",listed" : "") + ")");
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                Log.LogInfo("[OreRegrowth] ZNetScene fractured mining stages (" + names.Count + "): " +
                            (names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray())));
            }
            catch (Exception e)
            {
                Log.LogWarning("[OreRegrowth] could not enumerate fractured prefabs: " + e.Message);
            }
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

                // Dedupe guard: never stack a second vein on top of a node that is already there.
                // Both stages count - a standing rock4_copper OR a half-mined rock4_copper_frac at
                // that spot means this record is stale (double-recorded, or the world was rolled
                // back), so drop it instead of duplicating the ore.
                if (NodeAlreadyThere(e))
                {
                    _pending.RemoveAt(i);
                    _dirty = true;
                    Log.LogInfo("[OreRegrowth] dropped a stale record for " + e.name + " at " +
                                Fmt(e.Pos) + ": a node is already standing within " +
                                DedupeRadius.ToString("0.#") + "m");
                    continue;
                }

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

        /// <summary>
        /// True when a ZDO of the entry's prefab - or of that prefab's fractured stage - already
        /// exists within DedupeRadius of the recorded position. One linear pass over
        /// ZDOMan.m_objectsByID; only ever run for an entry that has already passed the day, tier
        /// and player-distance gates, at most MaxPerTick times per sweep.
        /// </summary>
        internal bool NodeAlreadyThere(RegrowthEntry e)
        {
            var man = ZDOMan.instance;
            if (man == null || man.m_objectsByID == null) return false;

            int frac;
            bool haveFrac = _fracOf.TryGetValue(e.prefabHash, out frac);
            float sq = DedupeRadius * DedupeRadius;
            var pos = e.Pos;

            foreach (var kv in man.m_objectsByID)
            {
                var z = kv.Value;
                if (z == null) continue;
                int p = z.GetPrefab();
                if (p != e.prefabHash && !(haveFrac && p == frac)) continue;
                if ((z.GetPosition() - pos).sqrMagnitude <= sq) return true;
            }
            return false;
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

        /// <summary>
        /// Proves the 0.4.5 contract end to end on a headless server, with no players:
        ///   1. a rock4_copper_frac ZDO destroyed through the real funnel records "rock4_copper";
        ///   2. the sweep then respawns the ORIGINAL vein, not the fractured stage;
        ///   3. a second, identical record at the same spot is dropped by the dedupe guard.
        /// Everything it creates is destroyed again, so the throwaway world is left as it was.
        /// </summary>
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

                const string record = "rock4_copper_frac";
                int recordHash = record.GetStableHashCode();
                RegrowthPrefab info;
                if (!_allow.TryGetValue(recordHash, out info))
                {
                    Log.LogWarning("[OreRegrowth][SelfTest] " + record +
                                   " is not in the allowlist, skipped");
                    return;
                }
                Log.LogInfo("[OreRegrowth][SelfTest] step 0: record=" + info.Name +
                            " respawn=" + info.RespawnName + " tier=" + info.Tier);

                // (1) somewhere real but empty: 25 m from an existing copper vein, so the dedupe
                //     guard has nothing to trip over and the spot is inside a loaded zone.
                ZDO anchor = null;
                int baseHash = info.RespawnHash;
                foreach (var kv in ZDOMan.instance.m_objectsByID)
                {
                    if (kv.Value != null && kv.Value.GetPrefab() == baseHash) { anchor = kv.Value; break; }
                }
                if (anchor == null)
                {
                    Log.LogWarning("[OreRegrowth][SelfTest] no existing " + info.RespawnName +
                                   " ZDO in the world (" + ZDOMan.instance.m_objectsByID.Count +
                                   " ZDOs), skipped");
                    return;
                }
                var pos = anchor.GetPosition() + new Vector3(0f, 0f, 25f);
                Log.LogInfo("[OreRegrowth][SelfTest] step 1: anchor " + info.RespawnName +
                            " zdo=" + anchor.m_uid + "; test spot " + Fmt(pos));

                // (2) build a fractured stage there and push it through the REAL destroy funnel.
                var fracEntry = new RegrowthEntry
                {
                    prefabHash = recordHash, name = record, tier = info.Tier, day = -999,
                    x = pos.x, y = pos.y, z = pos.z
                };
                var fracZdo = Respawn(fracEntry);
                if (fracZdo == null)
                {
                    Log.LogError("[OreRegrowth][SelfTest] step 2: FAIL - could not create a " +
                                 record + " ZDO");
                    return;
                }
                var fracId = fracZdo.m_uid;
                int before = _pending.Count;
                ZDOMan.instance.HandleDestroyedZDO(fracId);
                bool recorded = _pending.Count == before + 1;
                var entry = recorded ? _pending[_pending.Count - 1] : null;
                Log.LogInfo("[OreRegrowth][SelfTest] step 2: destroyed " + record + " zdo=" + fracId +
                            " -> " + (recorded ? "RECORDED as " + entry.name : "NOT RECORDED") +
                            ", zdo gone=" + (ZDOMan.instance.GetZDO(fracId) == null));
                if (!recorded)
                {
                    Log.LogError("[OreRegrowth][SelfTest] step 2: FAIL - the frac destroy was not recorded");
                    return;
                }
                if (entry.name != info.RespawnName || entry.prefabHash != info.RespawnHash)
                    Log.LogError("[OreRegrowth][SelfTest] step 2: FAIL - recorded " + entry.name +
                                 ", wanted the ORIGINAL vein " + info.RespawnName);

                // (3) age it and sweep.
                entry.day = -999;
                _dirty = true;
                Log.LogInfo("[OreRegrowth][SelfTest] step 3: today=" + CurrentDay() + " (" + DaySource() +
                            ") frontier: " + Frontier.Describe() + " IsBehind(" + entry.tier + ")=" +
                            Tiers.IsBehind(entry.tier));
                if (!Tiers.IsBehind(entry.tier))
                    Log.LogWarning("[OreRegrowth][SelfTest] tier " + entry.tier + " is NOT behind the " +
                                   "frontier right now, so the sweep will (correctly) refuse. Set " +
                                   "[Frontier] TierOverride >= " + (entry.tier + 1) + " to exercise the respawn.");

                int n = RunSweep();
                Log.LogInfo("[OreRegrowth][SelfTest] step 3: sweep respawned " + n + " node(s)");

                // (4) the thing that came back must be the ORIGINAL prefab, at the spot.
                ZDO fresh = null;
                foreach (var kv in ZDOMan.instance.m_objectsByID)
                {
                    var z = kv.Value;
                    if (z == null || z.GetPrefab() != info.RespawnHash) continue;
                    if ((z.GetPosition() - pos).sqrMagnitude > 1f) continue;
                    fresh = z; break;
                }
                if (fresh == null)
                {
                    Log.LogError("[OreRegrowth][SelfTest] step 4: FAIL - no new " + info.RespawnName +
                                 " ZDO within 1 m of " + Fmt(pos) + " (sweep respawned " + n + ")");
                }
                else
                {
                    var back = ZDOMan.instance.GetZDO(fresh.m_uid);
                    Log.LogInfo("[OreRegrowth][SelfTest] step 4: PASS - new zdo=" + fresh.m_uid +
                                " prefab=" + fresh.GetPrefab() + " (" + info.RespawnName + ") at " +
                                Fmt(fresh.GetPosition()) + " persistent=" + fresh.Persistent +
                                " distant=" + fresh.Distant + " type=" + fresh.Type +
                                " owner=" + fresh.GetOwner() + " GetZDO(round-trip)=" + (back != null));

                    // (5) dedupe: a second identical record must be dropped, not duplicated.
                    var dupe = new RegrowthEntry
                    {
                        prefabHash = info.RespawnHash, name = info.RespawnName, tier = info.Tier,
                        day = -999, x = pos.x, y = pos.y, z = pos.z
                    };
                    _pending.Add(dupe);
                    _dirty = true;
                    bool blocked = NodeAlreadyThere(dupe);
                    int n2 = RunSweep();
                    bool dropped = !_pending.Contains(dupe);
                    Log.LogInfo("[OreRegrowth][SelfTest] step 5: dedupe guard sees a node within " +
                                DedupeRadius.ToString("0.#") + "m = " + blocked +
                                ", sweep respawned " + n2 + " (want 0), stale record dropped=" + dropped +
                                (blocked && n2 == 0 && dropped ? "  PASS" : "  *** FAIL ***"));
                    _pending.Remove(dupe);

                    // (6) tidy up: the base vein is not a RECORD prefab, so destroying it leaves
                    //     no new entry behind - the throwaway world ends exactly as it started.
                    var freshId = fresh.m_uid;          // ZDOPool.Release resets m_uid, capture first
                    int p0 = _pending.Count;
                    ZDOMan.instance.HandleDestroyedZDO(freshId);
                    Log.LogInfo("[OreRegrowth][SelfTest] step 6: cleanup destroyed " + info.RespawnName +
                                " zdo=" + freshId + ", gone=" + (ZDOMan.instance.GetZDO(freshId) == null) +
                                ", new records=" + (_pending.Count - p0) + " (want 0)");
                }

                // Leave nothing behind in the store.
                _pending.Remove(entry);
                _dirty = true;
                SaveStore();
            }
            catch (Exception ex)
            {
                Log.LogError("[OreRegrowth][SelfTest] threw: " + ex);
            }
        }
    }

    /// <summary>
    /// One allowlisted ore prefab. Hash/Name are the RECORD prefab (the one whose ZDO dying means
    /// "mined out" - the fractured stage where there is one); RespawnHash/RespawnName are what the
    /// sweep puts back. They are equal for ores with no fractured stage.
    /// </summary>
    internal sealed class RegrowthPrefab
    {
        public int Hash;
        public string Name;
        public int Tier;
        public int RespawnHash;
        public string RespawnName;
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
