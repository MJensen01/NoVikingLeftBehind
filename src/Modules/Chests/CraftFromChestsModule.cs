// Adapted from AzuCraftyBoxes by Azumatt - https://github.com/AzumattDev/AzuCraftyBoxes (MIT-0).
// The choice of patch points (Player.HaveRequirements x2, Player.ConsumeResources,
// InventoryGui.SetupRequirement, Smelter.OnAddOre/OnAddFuel, Fireplace.Interact,
// CookingStation.OnAddFuelSwitch/FindCookableItem) follows that mod. The bodies are rewritten
// for this codebase: per-station toggles, measured consumption and an ownership guard.
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Crafting, upgrading and building may consume materials straight out of nearby chests, and
    /// smelters / fires may be fed from them, with one toggle per station family.
    ///
    /// COOKING STATIONS ARE OFF BY DEFAULT. Meat racks, the iron cooking station and the oven all
    /// carry a CookingStation component, and the group wants raw meat kept for recipes rather than
    /// silently vanishing onto the nearest rack. The hooks exist and are complete - they are simply
    /// gated on [Chests] PullForCookingStations, so the behaviour is a server-side config flip
    /// rather than a mod update.
    ///
    /// HOW IT COMPOSES WITH TrailingTierDiscount
    /// -----------------------------------------
    /// That module decides HOW MANY of a thing a recipe costs (a GetAmount postfix driven by a
    /// thread-static tier context, plus an in-place scaling of Piece.Requirement.m_amount for the
    /// build check). This module decides WHERE those items come from. They are kept honest by
    /// Harmony's execution order:
    ///
    ///   prefixes -> original -> POSTFIXES -> finalizers
    ///
    /// Discount sets its context in a prefix and clears it in a FINALIZER, so every one of our
    /// POSTFIXES still sees discounted numbers - GetAmount() and Piece.Requirement.m_amount both
    /// read discounted while we run. And ConsumeResources is handled by a non-skipping prefix that
    /// only records inventory counts plus a postfix that pulls the shortfall: we never call
    /// GetAmount to decide how much left the player's inventory, we MEASURE it, so no ordering
    /// between the two modules (or a third mod) can make the counted, shown and consumed numbers
    /// disagree. The two modules also live in different declaring types, which is what keeps their
    /// Harmony __state slots separate.
    /// </summary>
    internal sealed class CraftFromChestsModule : FeatureModule
    {
        public override string Name => "CraftFromChests";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Chests";

        private static CraftFromChestsModule _self;

        private static ConfigEntry<float> _range;
        private static ConfigEntry<bool> _pullCrafting;
        private static ConfigEntry<bool> _pullBuilding;
        private static ConfigEntry<bool> _pullSmelters;
        private static ConfigEntry<bool> _pullFires;
        private static ConfigEntry<bool> _pullCooking;
        private static ConfigEntry<bool> _leaveOne;
        private static ConfigEntry<bool> _includeVehicles;
        private static ConfigEntry<string> _excludedContainers;
        private static ConfigEntry<string> _excludedItems;
        private static ConfigEntry<bool> _showNearbyCount;
        private static ConfigEntry<string> _toggleKey;

        /// <summary>Per-player kill switch behind ToggleKey. Never synced, never persisted.</summary>
        private static bool _userOn = true;

        private static KeyCode _keyMain = KeyCode.None;
        private static KeyCode[] _keyMods = new KeyCode[0];

        internal static bool PullCooking => _pullCooking != null && _pullCooking.Value;

        /// <summary>Patches installed, config on, we are a client, and the player has not toggled off.</summary>
        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive() && _userOn && Player.m_localPlayer != null;
        }

        // ---- config -------------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _range = BindSynced("Range", 20f,
                "How far a container may be from the player and still count, in metres.");

            _pullCrafting = BindSynced("PullForCrafting", true,
                "Recipes in the crafting / forge / workbench GUI, upgrades included, may take " +
                "their materials from nearby containers.");

            _pullBuilding = BindSynced("PullForBuilding", true,
                "Build pieces placed with the hammer (and the hoe/cultivator) may take their " +
                "materials from nearby containers.");

            _pullSmelters = BindSynced("PullForSmelters", true,
                "Smelter, blast furnace, charcoal kiln, windmill, spinning wheel: interacting " +
                "with one may pull its ore and its fuel from nearby containers.");

            _pullFires = BindSynced("PullForFires", true,
                "Fireplaces, hearths, bonfires and standing torches may pull their fuel from " +
                "nearby containers.");

            _pullCooking = BindSynced("PullForCookingStations", false,
                "COOKING STATIONS: meat racks, the iron cooking station and the oven. FALSE by " +
                "default on purpose - the group keeps raw meat for recipes, and an auto-feeding " +
                "rack empties the chests. Covers BOTH the cookable item and the station's fuel.");

            _leaveOne = BindSynced("LeaveOneItem", false,
                "Always leave one of an item behind in a container instead of emptying the stack. " +
                "Useful if you sort chests by what is in them.");

            _includeVehicles = BindSynced("IncludeVehicles", true,
                "Count the cargo of carts and ships as nearby containers. A cart currently being " +
                "pulled is skipped either way.");

            _excludedContainers = BindSynced("ExcludedContainers", "piece_chest_private",
                "Container PREFAB names that are never pulled from, comma-separated. " +
                "Example: piece_chest_private, piece_chest_wood");

            _excludedItems = BindSynced("ExcludedItems", "",
                "Item prefab names that are never pulled out of a container, comma-separated. " +
                "They still count from the player's own inventory. Example: FineWood, Coins");

            _showNearbyCount = BindSynced("ShowNearbyCount", true,
                "Show the requirement rows in the crafting and build UI as have/needed, where " +
                "'have' includes nearby containers, instead of just the required number.");

            _toggleKey = BindLocal("ToggleKey", "LeftAlt+O",
                "MACHINE-LOCAL. Key combination that turns this player's own container pulling on " +
                "and off, with a HUD message. Format: optional modifiers then the key, joined by " +
                "'+', using Unity KeyCode names - LeftAlt+O, LeftControl+LeftShift+K, F7, or " +
                "None to disable the hotkey. A modifier is recommended so it cannot fire while " +
                "you are typing in chat.");

            PushSettings();
        }

        private static void PushSettings()
        {
            ChestSource.Range = Mathf.Max(0f, _range.Value);
            ChestSource.LeaveOne = _leaveOne.Value;
            ChestSource.IncludeVehicles = _includeVehicles.Value;
            ChestSource.ExcludedContainers = ChestSource.ParseNames(_excludedContainers.Value);
            ChestSource.ExcludedItems = ChestSource.ParseNames(_excludedItems.Value);
            ParseKey(_toggleKey.Value);
        }

        private static void ParseKey(string spec)
        {
            _keyMain = KeyCode.None;
            _keyMods = new KeyCode[0];
            if (string.IsNullOrEmpty(spec)) return;

            var parts = spec.Split('+');
            var mods = new List<KeyCode>();
            for (int i = 0; i < parts.Length; i++)
            {
                var s = parts[i].Trim();
                if (s.Length == 0) continue;
                KeyCode k;
                try { k = (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
                catch
                {
                    Log.LogWarning("[Chests] ToggleKey: '" + s + "' is not a Unity KeyCode - hotkey disabled");
                    _keyMain = KeyCode.None;
                    _keyMods = new KeyCode[0];
                    return;
                }
                if (i == parts.Length - 1) _keyMain = k;
                else mods.Add(k);
            }
            if (_keyMain == KeyCode.None) mods.Clear();
            _keyMods = mods.ToArray();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            PushSettings();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        // ---- patches --------------------------------------------------------------------------------

        private static HarmonyMethod M(string name)
        {
            return new HarmonyMethod(typeof(CraftFromChestsModule), name);
        }

        private void Need(ref System.Reflection.MethodInfo slot, Type t, string name, Type[] args, string label)
        {
            slot = args == null ? AccessTools.DeclaredMethod(t, name) : AccessTools.DeclaredMethod(t, name, args);
            if (slot == null) throw new Exception(label + " not found");
        }

        protected override void ApplyPatches()
        {
            System.Reflection.MethodInfo m = null;

            // --- registry -----------------------------------------------------------------------
            Need(ref m, typeof(Container), "Awake", null, "Container.Awake()");
            Harmony.Patch(m, postfix: M(nameof(ContainerAwakePost)));

            Need(ref m, typeof(Container), "OnDestroyed", null, "Container.OnDestroyed()");
            Harmony.Patch(m, postfix: M(nameof(ContainerDestroyedPost)));

            // --- crafting / upgrading -----------------------------------------------------------
            Need(ref m, typeof(Player), "HaveRequirements",
                 new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) },
                 "Player.HaveRequirements(Recipe,bool,int,int)");
            Harmony.Patch(m, postfix: M(nameof(HaveRecipePost)));

            // --- building -------------------------------------------------------------------------
            Need(ref m, typeof(Player), "HaveRequirements",
                 new[] { typeof(Piece), typeof(Player.RequirementMode) },
                 "Player.HaveRequirements(Piece,RequirementMode)");
            Harmony.Patch(m, postfix: M(nameof(HavePiecePost)));

            // --- what actually leaves the world ---------------------------------------------------
            Need(ref m, typeof(Player), "ConsumeResources",
                 new[] { typeof(Piece.Requirement[]), typeof(int), typeof(int), typeof(int) },
                 "Player.ConsumeResources(Requirement[],int,int,int)");
            Harmony.Patch(m, prefix: M(nameof(ConsumePre)), postfix: M(nameof(ConsumePost)));

            // --- the cost rows in the crafting panel and the build hud ---------------------------
            Need(ref m, typeof(InventoryGui), "SetupRequirement",
                 new[] { typeof(Transform), typeof(Piece.Requirement), typeof(Player), typeof(bool), typeof(int), typeof(int) },
                 "InventoryGui.SetupRequirement(Transform,Requirement,Player,bool,int,int)");
            Harmony.Patch(m, postfix: M(nameof(SetupRequirementPost)));

            // --- smelter family --------------------------------------------------------------------
            Need(ref m, typeof(Smelter), "OnAddOre",
                 new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "Smelter.OnAddOre");
            Harmony.Patch(m, prefix: M(nameof(SmelterAddOrePre)));

            Need(ref m, typeof(Smelter), "OnAddFuel",
                 new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "Smelter.OnAddFuel");
            Harmony.Patch(m, prefix: M(nameof(SmelterAddFuelPre)));

            // --- fires ------------------------------------------------------------------------------
            Need(ref m, typeof(Fireplace), "Interact",
                 new[] { typeof(Humanoid), typeof(bool), typeof(bool) }, "Fireplace.Interact");
            Harmony.Patch(m, prefix: M(nameof(FireplaceInteractPre)));

            // --- cooking stations: installed, but gated on PullForCookingStations (false) ----------
            Need(ref m, typeof(CookingStation), "OnAddFuelSwitch",
                 new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "CookingStation.OnAddFuelSwitch");
            Harmony.Patch(m, prefix: M(nameof(CookingAddFuelPre)));

            Need(ref m, typeof(CookingStation), "FindCookableItem",
                 new[] { typeof(Inventory) }, "CookingStation.FindCookableItem");
            Harmony.Patch(m, postfix: M(nameof(CookingFindCookablePost)));

            // --- the local on/off hotkey -------------------------------------------------------------
            Need(ref m, typeof(Player), "Update", null, "Player.Update()");
            Harmony.Patch(m, postfix: M(nameof(PlayerUpdatePost)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override void Disable()
        {
            base.Disable();
            ChestSource.Clear();
        }

        // ---- registry ---------------------------------------------------------------------------------

        private static void ContainerAwakePost(Container __instance)
        {
            if (_self == null || !_self.Enabled || !ClientActive()) return;
            ChestSource.Register(__instance);
        }

        private static void ContainerDestroyedPost(Container __instance)
        {
            ChestSource.Unregister(__instance);
        }

        // ---- the hotkey --------------------------------------------------------------------------------

        private static void PlayerUpdatePost(Player __instance)
        {
            if (_self == null || !_self.Active || !ClientActive()) return;
            if (__instance != Player.m_localPlayer) return;
            if (_keyMain == KeyCode.None) return;
            if (!Input.GetKeyDown(_keyMain)) return;
            for (int i = 0; i < _keyMods.Length; i++)
                if (!Input.GetKey(_keyMods[i])) return;

            _userOn = !_userOn;
            var msg = _userOn
                ? "Craft from chests: ON"
                : "Craft from chests: OFF (this character only)";
            Log.LogInfo("[Chests] " + msg);
            try { __instance.Message(MessageHud.MessageType.Center, msg); }
            catch { /* no hud yet */ }
        }

        // ---- crafting: can I make this? -----------------------------------------------------------------

        private static void HaveRecipePost(Player __instance, Recipe recipe, bool discover,
                                           int qualityLevel, int amount, ref bool __result)
        {
            if (__result || discover) return;
            if (!Live() || !_pullCrafting.Value) return;
            if (__instance != Player.m_localPlayer) return;
            if (recipe == null || recipe.m_resources == null || recipe.m_item == null) return;

            // "Only one ingredient" recipes pick a concrete ItemData in Player.GetFirstRequiredItem
            // and DoCrafting silently does nothing when that comes back null. Saying "yes you can"
            // here without also producing that item would give a dead craft button, so these stay
            // vanilla: they craft from the player's own inventory only.
            if (recipe.m_requireOnlyOneIngredient) return;

            try
            {
                // Vanilla returned false; it may have been the station or the DLC, not the items.
                if (!__instance.RequiredCraftingStation(recipe, qualityLevel, true)) return;
                var dlc = recipe.m_item.m_itemData.m_shared.m_dlc;
                if (dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(dlc)) return;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (boxes.Count == 0) return;

                foreach (var req in recipe.m_resources)
                {
                    if (req == null || !req.m_resItem) continue;
                    int need = req.GetAmount(qualityLevel) * amount;
                    if (need <= 0) continue;
                    if (Available(__instance, req, need, boxes) < need) return;
                }

                __result = true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] HaveRequirements(Recipe) postfix: " + e.Message);
            }
        }

        /// <summary>
        /// Vanilla takes the max over quality levels rather than the sum, so we mirror that exactly
        /// and just add the containers into each level. Craft materials are quality 1, so for every
        /// real recipe this is simply "inventory + chests".
        /// </summary>
        private static int Available(Player p, Piece.Requirement req, int need, List<Box> boxes)
        {
            string shared = req.m_resItem.m_itemData.m_shared.m_name;
            string prefab = Utils.GetPrefabName(req.m_resItem.gameObject);
            bool blocked = ChestSource.ItemBlocked(prefab, shared);

            int best = 0;
            int maxQ = Mathf.Max(1, req.m_resItem.m_itemData.m_shared.m_maxQuality);
            for (int q = 1; q <= maxQ; q++)
            {
                int have = p.m_inventory.CountItems(shared, q);
                if (!blocked && have < need) have += ChestSource.Count(shared, boxes, q);
                if (have > best) best = have;
                if (best >= need) break;
            }
            return best;
        }

        // ---- building: can I place this? -------------------------------------------------------------

        private static void HavePiecePost(Player __instance, Piece piece, Player.RequirementMode mode,
                                          ref bool __result)
        {
            if (__result) return;
            if (mode != Player.RequirementMode.CanBuild && mode != Player.RequirementMode.CanAlmostBuild) return;
            if (!Live() || !_pullBuilding.Value) return;
            if (__instance != Player.m_localPlayer) return;
            if (piece == null || piece.m_resources == null) return;

            try
            {
                if (piece.m_craftingStation &&
                    !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, __instance.transform.position) &&
                    !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench)) return;
                if (piece.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(piece.m_dlc)) return;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (boxes.Count == 0) return;

                foreach (var req in piece.m_resources)
                {
                    if (req == null || !req.m_resItem || req.m_amount <= 0) continue;

                    // m_amount, not GetAmount: this is the field vanilla's CanBuild branch reads,
                    // and it is the field TrailingTierDiscount has already scaled in its prefix.
                    string shared = req.m_resItem.m_itemData.m_shared.m_name;
                    string prefab = Utils.GetPrefabName(req.m_resItem.gameObject);
                    int need = mode == Player.RequirementMode.CanAlmostBuild ? 1 : req.m_amount;

                    int have = __instance.m_inventory.CountItems(shared);
                    if (have < need && !ChestSource.ItemBlocked(prefab, shared))
                        have += ChestSource.Count(shared, boxes);
                    if (have < need) return;
                }

                __result = true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] HaveRequirements(Piece) postfix: " + e.Message);
            }
        }

        // ---- consuming -----------------------------------------------------------------------------------

        /// <summary>
        /// Records what the player is carrying BEFORE vanilla removes anything. Never skips the
        /// original and never touches the numbers, so it composes with any other prefix.
        /// </summary>
        private static void ConsumePre(Player __instance, Piece.Requirement[] requirements, out int[] __state)
        {
            __state = null;
            if (!Live()) return;
            if (!_pullCrafting.Value && !_pullBuilding.Value) return;
            if (__instance != Player.m_localPlayer || requirements == null) return;

            var pre = new int[requirements.Length];
            for (int i = 0; i < requirements.Length; i++)
            {
                var r = requirements[i];
                pre[i] = (r != null && r.m_resItem)
                    ? __instance.m_inventory.CountItems(r.m_resItem.m_itemData.m_shared.m_name)
                    : 0;
            }
            __state = pre;
        }

        /// <summary>
        /// Vanilla has just taken what the player had. Anything still owed comes out of the chests.
        /// The amount taken from the player is measured, not recomputed, so whatever
        /// TrailingTierDiscount (or any other mod) decided the cost was, we finish exactly that bill.
        /// </summary>
        private static void ConsumePost(Player __instance, Piece.Requirement[] requirements,
                                        int qualityLevel, int itemQuality, int multiplier, int[] __state)
        {
            if (__state == null || requirements == null) return;

            try
            {
                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (boxes.Count == 0) return;

                for (int i = 0; i < requirements.Length && i < __state.Length; i++)
                {
                    var r = requirements[i];
                    if (r == null || !r.m_resItem) continue;

                    int need = r.GetAmount(qualityLevel) * multiplier;
                    if (need <= 0) continue;

                    string shared = r.m_resItem.m_itemData.m_shared.m_name;
                    string prefab = Utils.GetPrefabName(r.m_resItem.gameObject);
                    if (ChestSource.ItemBlocked(prefab, shared)) continue;

                    int tookFromPlayer = __state[i] - __instance.m_inventory.CountItems(shared);
                    int owed = need - tookFromPlayer;
                    if (owed <= 0) continue;

                    int got = ChestSource.Consume(shared, owed, itemQuality, boxes);
                    if (got < owed)
                        Log.LogWarning("[Chests] only " + got + "/" + owed + " " + prefab +
                                       " came out of nearby containers - the recipe was charged short");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] ConsumeResources postfix: " + e.Message);
            }
        }

        // ---- the cost rows ----------------------------------------------------------------------------------

        private static void SetupRequirementPost(Transform elementRoot, Piece.Requirement req, Player player,
                                                 bool craft, int quality, int craftMultiplier, bool __result)
        {
            if (!__result) return;
            if (!Live() || !_showNearbyCount.Value) return;
            if (player != Player.m_localPlayer) return;
            if (req == null || !req.m_resItem) return;
            if (!(craft ? _pullCrafting.Value : _pullBuilding.Value)) return;

            try
            {
                var amountText = elementRoot.Find("res_amount");
                if (amountText == null) return;
                var tmp = amountText.GetComponent<TMP_Text>();
                if (tmp == null) return;

                // Read the number vanilla (and any cost-changing mod ahead of us) just wrote,
                // so the row can never show a different cost from the one that gets charged.
                int need;
                if (!int.TryParse(tmp.text, out need)) need = req.GetAmount(quality) * craftMultiplier;
                if (need <= 0) return;

                string shared = req.m_resItem.m_itemData.m_shared.m_name;
                string prefab = Utils.GetPrefabName(req.m_resItem.gameObject);

                int have = player.m_inventory.CountItems(shared);
                if (!ChestSource.ItemBlocked(prefab, shared))
                    have += ChestSource.Count(shared, ChestSource.Nearby(player.transform.position));

                tmp.text = have + "/" + need;
                if (have >= need) tmp.color = Color.white;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] SetupRequirement postfix: " + e.Message);
            }
        }

        // ---- smelters ------------------------------------------------------------------------------------------

        private static bool SmelterAddOrePre(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
                                             ref bool __result)
        {
            if (!Live() || !_pullSmelters.Value) return true;
            if (item != null || user == null || user != (Humanoid)Player.m_localPlayer) return true;

            try
            {
                var inv = user.GetInventory();
                if (inv == null) return true;
                if (__instance.GetQueueSize() >= __instance.m_maxOre) return true;   // vanilla says "it's full"

                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid()) return true;

                // If the player is carrying something processable, vanilla handles it.
                foreach (var conv in __instance.m_conversion)
                    if (conv != null && conv.m_from != null &&
                        inv.HaveItem(conv.m_from.m_itemData.m_shared.m_name)) return true;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (boxes.Count == 0) return true;

                foreach (var conv in __instance.m_conversion)
                {
                    if (conv == null || conv.m_from == null) continue;
                    string shared = conv.m_from.m_itemData.m_shared.m_name;
                    string prefab = Utils.GetPrefabName(conv.m_from.gameObject);
                    if (ChestSource.ItemBlocked(prefab, shared)) continue;
                    if (ChestSource.Consume(shared, 1, -1, boxes) != 1) continue;

                    user.Message(MessageHud.MessageType.Center, "$msg_added " + shared);
                    nview.InvokeRPC("RPC_AddOre", prefab);
                    __result = true;
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] Smelter.OnAddOre prefix: " + e.Message);
            }
            return true;
        }

        private static bool SmelterAddFuelPre(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
                                              ref bool __result)
        {
            if (!Live() || !_pullSmelters.Value) return true;
            if (user == null || user != (Humanoid)Player.m_localPlayer) return true;
            if (__instance.m_fuelItem == null) return true;

            try
            {
                string shared = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                if (item != null && item.m_shared.m_name != shared) return true;      // "$msg_wrongitem"
                if (__instance.GetFuel() > __instance.m_maxFuel - 1) return true;     // "$msg_itsfull"

                var inv = user.GetInventory();
                if (inv == null || inv.HaveItem(shared)) return true;                 // vanilla can do it

                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid()) return true;

                string prefab = Utils.GetPrefabName(__instance.m_fuelItem.gameObject);
                if (ChestSource.ItemBlocked(prefab, shared)) return true;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (ChestSource.Consume(shared, 1, -1, boxes) != 1) return true;

                user.Message(MessageHud.MessageType.Center, "$msg_added " + shared);
                nview.InvokeRPC("RPC_AddFuel");
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] Smelter.OnAddFuel prefix: " + e.Message);
            }
            return true;
        }

        // ---- fires ---------------------------------------------------------------------------------------------

        private static bool FireplaceInteractPre(Fireplace __instance, Humanoid user, bool hold, bool alt,
                                                 ref bool __result)
        {
            if (!Live() || !_pullFires.Value) return true;
            if (hold || user == null || user != (Humanoid)Player.m_localPlayer) return true;
            if (__instance.m_infiniteFuel || !__instance.m_canRefill || __instance.m_fuelItem == null) return true;

            try
            {
                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid()) return true;

                float fuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel);

                // Vanilla's "click to put the fire out" branch must win.
                if (__instance.m_canTurnOff && !alt && fuel > 0f) return true;
                if (Mathf.CeilToInt(fuel) >= __instance.m_maxFuel) return true;       // "$msg_cantaddmore"

                var inv = user.GetInventory();
                string shared = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                if (inv == null || inv.HaveItem(shared)) return true;                 // vanilla can do it

                string prefab = Utils.GetPrefabName(__instance.m_fuelItem.gameObject);
                if (ChestSource.ItemBlocked(prefab, shared)) return true;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (ChestSource.Consume(shared, 1, -1, boxes) != 1) return true;      // "$msg_outof"

                if (!nview.HasOwner()) nview.ClaimOwnership();
                user.Message(MessageHud.MessageType.Center,
                             Localization.instance.Localize("$msg_fireadding", shared));
                nview.InvokeRPC("RPC_AddFuel");
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] Fireplace.Interact prefix: " + e.Message);
            }
            return true;
        }

        // ---- cooking stations: complete, and off by default ------------------------------------------------------

        private static bool CookingAddFuelPre(CookingStation __instance, Humanoid user, ItemDrop.ItemData item,
                                              ref bool __result)
        {
            if (!Live() || !PullCooking) return true;
            if (user == null || user != (Humanoid)Player.m_localPlayer) return true;
            if (__instance.m_fuelItem == null) return true;

            try
            {
                string shared = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                if (item != null && item.m_shared.m_name != shared) return true;
                if (__instance.GetFuel() > __instance.m_maxFuel - 1) return true;

                var inv = user.GetInventory();
                if (inv == null || inv.HaveItem(shared)) return true;

                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid()) return true;

                string prefab = Utils.GetPrefabName(__instance.m_fuelItem.gameObject);
                if (ChestSource.ItemBlocked(prefab, shared)) return true;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (ChestSource.Consume(shared, 1, -1, boxes) != 1) return true;

                user.Message(MessageHud.MessageType.Center, "$msg_added " + shared);
                nview.InvokeRPC("RPC_AddFuel");
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] CookingStation.OnAddFuelSwitch prefix: " + e.Message);
            }
            return true;
        }

        private static void CookingFindCookablePost(CookingStation __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null) return;
            if (!Live() || !PullCooking) return;

            try
            {
                // Only pull if the station will actually accept it - vanilla checks these in
                // OnUseItem AFTER this call, and an item taken for a cook that then fails is gone.
                if (__instance.m_requireFire && !__instance.IsFireLit()) return;
                if (__instance.GetFreeSlot() == -1) return;

                var boxes = ChestSource.Nearby(__instance.transform.position);
                if (boxes.Count == 0) return;

                foreach (var conv in __instance.m_conversion)
                {
                    if (conv == null || conv.m_from == null) continue;
                    string shared = conv.m_from.m_itemData.m_shared.m_name;
                    string prefab = Utils.GetPrefabName(conv.m_from.gameObject);
                    if (ChestSource.ItemBlocked(prefab, shared)) continue;
                    if (ChestSource.Count(shared, boxes) <= 0) continue;
                    if (ChestSource.Consume(shared, 1, -1, boxes) != 1) continue;

                    // A detached clone: vanilla's later RemoveItem on the player's inventory finds
                    // nothing and harmlessly no-ops, and the container has already been debited.
                    var data = conv.m_from.m_itemData.Clone();
                    data.m_stack = 1;
                    data.m_dropPrefab = conv.m_from.gameObject;
                    __result = data;
                    return;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Chests] CookingStation.FindCookableItem postfix: " + e.Message);
            }
        }

        // ---- reporting -------------------------------------------------------------------------------------------

        internal static string Numbers()
        {
            var sb = new StringBuilder();
            sb.Append("range ").Append(_range.Value.ToString("0.#")).Append("m")
              .Append(", crafting=").Append(_pullCrafting.Value)
              .Append(" building=").Append(_pullBuilding.Value)
              .Append(" smelters=").Append(_pullSmelters.Value)
              .Append(" fires=").Append(_pullFires.Value)
              .Append(" cookingStations=").Append(_pullCooking.Value)
              .Append(_pullCooking.Value ? "" : " (meat stays in the chests)")
              .Append(", leaveOne=").Append(_leaveOne.Value)
              .Append(" vehicles=").Append(_includeVehicles.Value)
              .Append(" showCounts=").Append(_showNearbyCount.Value)
              .Append(", excludedContainers=").Append(ChestSource.ExcludedContainers.Count)
              .Append(" excludedItems=").Append(ChestSource.ExcludedItems.Count)
              .Append(", toggleKey=").Append(_toggleKey.Value);
            return sb.ToString();
        }

        public override string StatusDetail()
        {
            return Numbers() + ", registered containers=" + ChestSource.Registered +
                   (_userOn ? "" : ", TOGGLED OFF by this player");
        }
    }
}
