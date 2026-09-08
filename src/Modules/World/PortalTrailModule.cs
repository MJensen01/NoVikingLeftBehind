using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Lets ore/metal that is behind the frontier travel through portals; the newest tier still
    /// cannot. Vanilla blocks a portal trip when ANY item in the traveller's inventory has
    /// m_shared.m_teleportable == false (verified 0.221.12: Inventory.IsTeleportable() loops
    /// m_inventory and returns false on the first non-teleportable item, short-circuiting true
    /// when the world key GlobalKeys.TeleportAll is set; Humanoid.IsTeleportable() /
    /// Player.IsTeleportable() just call m_inventory.IsTeleportable(); TeleportWorld.Teleport()
    /// calls player.IsTeleportable() and shows "$msg_noteleport" on false).
    ///
    /// This is a PREFIX that fully replaces Inventory.IsTeleportable() (returns false to skip the
    /// original) but only ever RELAXES the vanilla rule: TeleportAll is honoured exactly as
    /// vanilla, and any item vanilla already allows is still allowed. The only thing this module
    /// changes is what happens to an item vanilla would otherwise block - it becomes allowed if
    /// [Portals] NeverAllow does not name it, and either [Portals] AlwaysAllow names it, or
    /// AllowBehindFrontier is on and its material tier is behind the frontier (Tiers.IsBehind,
    /// with an optional extra margin). So the answer can never be MORE restrictive than vanilla.
    ///
    /// Deliberately does NOT mutate ItemDrop.ItemData.m_shared.m_teleportable: that field lives on
    /// the shared per-prefab template (every ItemData.Clone() of the same prefab points at the
    /// same SharedData object - MemberwiseClone only copies the reference), so writing to it would
    /// silently change the answer everywhere else that field is read (other UI, other mods,
    /// ItemDrop.SetupItem re-templating), not just at the portal. Recomputing the answer at the
    /// moment a portal asks is a pure, side-effect-free check instead.
    ///
    /// Only ever evaluated for the LOCAL player's own inventory (Player.m_localPlayer.GetInventory()):
    /// each client's own TeleportWorld.UpdatePortal()/Teleport() asks its own Player.IsTeleportable(),
    /// so there is nothing to gate for anyone else's inventory on this client, and gating on it
    /// keeps this module inert for every other Humanoid.IsTeleportable() caller (tamed creatures
    /// etc. do not use portals, but this keeps the patch scoped to the one case that matters).
    /// </summary>
    internal sealed class PortalTrailModule : FeatureModule
    {
        public override string Name => "PortalTrail";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Portals";

        public override string Theme => "World";

        public override string Hint => "Let ore behind the frontier travel through portals";

        private ConfigEntry<bool> _allowBehindFrontier;
        private ConfigEntry<int> _extraTiersBehind;
        private ConfigEntry<string> _alwaysAllow;
        private ConfigEntry<string> _neverAllow;

        private static PortalTrailModule _self;
        private static HashSet<string> _alwaysSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _neverSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool Live() { return _self != null && _self.Active && ClientActive(); }

        protected override void Bind()
        {
            _self = this;

            _allowBehindFrontier = BindSynced("AllowBehindFrontier", true,
                "A non-teleportable item (m_shared.m_teleportable == false, e.g. raw ore) is " +
                "still let through a portal if its material tier is behind the frontier " +
                "(Tiers.IsBehind, same rule every other catch-up module uses). The newest tier " +
                "the group is still actively farming stays blocked, same as vanilla.",
                Opt.B("Let ore behind the frontier ride a portal"));

            _extraTiersBehind = BindSynced("ExtraTiersBehind", 0,
                "Extra margin on top of [Frontier] TiersBehind before a material's raw ore counts " +
                "as 'behind' for portal purposes specifically. 0 = the same cutoff every other " +
                "catch-up module uses (WorldTier - TiersBehind). 1 = one tier further back than " +
                "that, i.e. requires two tiers behind before ore may ride a portal.",
                Opt.N("Extra tiers behind before ore is allowed through a portal", 0, 5, 1));

            _alwaysAllow = BindSynced("AlwaysAllow", "",
                "Comma-separated prefab names always allowed through a portal regardless of tier " +
                "or vanilla m_teleportable (e.g. a quest item another mod flagged non-teleportable). " +
                "Empty by default.",
                Opt.T("Items always allowed through a portal")
                    .Pick(new PickerSpec(PickerSource.Items)));

            _neverAllow = BindSynced("NeverAllow", "",
                "Comma-separated prefab names that may never ride a portal even if the tier rule " +
                "would otherwise allow them, and even if vanilla itself would allow them - takes " +
                "priority over everything else, including AlwaysAllow. Empty by default.",
                Opt.T("Items that may never ride a portal")
                    .Pick(new PickerSpec(PickerSource.Items)));

            ParseLists();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _alwaysAllow || entry == _neverAllow) ParseLists();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private void ParseLists()
        {
            _alwaysSet = ParseNames(_alwaysAllow != null ? _alwaysAllow.Value : "");
            _neverSet = ParseNames(_neverAllow != null ? _neverAllow.Value : "");
        }

        private static HashSet<string> ParseNames(string raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var chunk in raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = chunk.Trim();
                if (name.Length > 0) set.Add(name);
            }
            return set;
        }

        protected override void ApplyPatches()
        {
            var m = AccessTools.Method(typeof(Inventory), "IsTeleportable", Type.EmptyTypes);
            if (m == null) throw new Exception("Inventory.IsTeleportable() not found");
            Harmony.Patch(m, prefix: new HarmonyMethod(typeof(PortalTrailModule), nameof(IsTeleportablePrefix)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        /// <summary>
        /// Replaces Inventory.IsTeleportable() (skips the original) but only ever relaxes it: see
        /// the class doc for why this can never be MORE restrictive than vanilla.
        /// </summary>
        private static bool IsTeleportablePrefix(Inventory __instance, ref bool __result)
        {
            if (!Live()) return true;                        // run vanilla unmodified
            if (!IsLocalPlayerInventory(__instance)) return true;

            var zs = ZoneSystem.instance;
            if (zs != null && zs.GetGlobalKey(GlobalKeys.TeleportAll))
            {
                __result = true;
                return false; // skip original
            }

            bool extra = _self._allowBehindFrontier.Value;
            int margin = _self._extraTiersBehind.Value;

            foreach (var item in __instance.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                string name = Tiers.CleanName(item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name);

                if (_neverSet.Contains(name))
                {
                    __result = false;
                    return false;
                }

                if (item.m_shared.m_teleportable) continue;           // vanilla already says yes
                if (_alwaysSet.Contains(name)) continue;
                if (extra && IsBehindWithMargin(Tiers.OfItem(name), margin)) continue;

                __result = false;
                return false;
            }

            __result = true;
            return false;
        }

        private static bool IsBehindWithMargin(int tier, int margin)
        {
            if (tier < 1) return false;
            int cutoff = Frontier.WorldTier - (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1) - Math.Max(0, margin);
            return tier <= cutoff;
        }

        private static bool IsLocalPlayerInventory(Inventory inv)
        {
            var p = Player.m_localPlayer;
            return p != null && (object)p.GetInventory() == inv;
        }

        private string Numbers()
        {
            return "AllowBehindFrontier=" + _allowBehindFrontier.Value +
                   " ExtraTiersBehind=" + _extraTiersBehind.Value +
                   " behind-the-frontier tiers: " + BehindRangeText(_extraTiersBehind.Value) +
                   " AlwaysAllow=[" + string.Join(",", new List<string>(_alwaysSet).ToArray()) + "]" +
                   " NeverAllow=[" + string.Join(",", new List<string>(_neverSet).ToArray()) + "]";
        }

        private static string BehindRangeText(int margin)
        {
            int max = Frontier.WorldTier - (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1) - Math.Max(0, margin);
            return max < 1 ? "none" : "1.." + max;
        }

        public override string StatusDetail() { return Numbers(); }

        /// <summary>Headless proof: which of a fixed probe list of material tiers currently pass.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            bool extra = _self != null && _self._allowBehindFrontier.Value;
            int margin = _self != null ? _self._extraTiersBehind.Value : 0;
            sb.Append("[SelfTest][PortalTrail] AllowBehindFrontier=").Append(extra)
              .Append(" ExtraTiersBehind=").Append(margin)
              .Append(" WorldTier=").Append(Frontier.Describe())
              .Append(" TiersBehind=").Append(Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1);

            string[] probe = { "Copper", "Tin", "Bronze", "Iron", "Silver", "BlackMetal", "DragonEgg" };
            foreach (var name in probe)
            {
                int tier = Tiers.OfItem(name);
                bool vanillaTeleportable = VanillaTeleportable(name);
                bool never = _neverSet.Contains(name);
                bool passes;
                if (never) passes = false;
                else if (vanillaTeleportable) passes = true;
                else if (_alwaysSet.Contains(name)) passes = true;
                else passes = extra && IsBehindWithMargin(tier, margin);

                sb.Append("\n  ").Append(name).Append(" tier=").Append(tier)
                  .Append(" vanillaTeleportable=").Append(vanillaTeleportable)
                  .Append(" -> ").Append(passes ? "PASS" : "blocked");
            }
            return sb.ToString();
        }

        private static bool VanillaTeleportable(string prefabName)
        {
            var odb = ObjectDB.instance;
            if (odb == null) return true;
            var go = odb.GetItemPrefab(prefabName);
            var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            return drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null
                       || drop.m_itemData.m_shared.m_teleportable;
        }
    }
}
