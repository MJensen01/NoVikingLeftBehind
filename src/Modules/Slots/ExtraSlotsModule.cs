using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Extra inventory slots: equipment (head / chest / legs / cape), utility, food, ammo and
    /// plain storage slots. Replaces shudnal's ExtraSlots, and rescues characters migrating from it.
    ///
    /// Client-side: the whole feature is one player's UI and one player's profile. The slot COUNTS
    /// are BindSynced anyway, so the dedicated server owns the numbers and writes them into its own
    /// config file (FeatureModule.Configure runs for every module regardless of Side; only
    /// TryEnable checks the side). That is what stops two players on the same server disagreeing
    /// about the grid, which QOL-ARCHITECTURE section 4 rule 5 calls out as the other loss vector.
    ///
    /// The slots are real cells of the player's own Inventory (height grown from 4 to 4 + rows),
    /// so drag and drop, tooltips, durability bars, weight and Humanoid.EquipItem all work with no
    /// code from us - see SlotLayout for why. They are NEVER written into the vanilla package; see
    /// SlotStore and SlotBlob.
    /// </summary>
    internal sealed class ExtraSlotsModule : FeatureModule
    {
        public override string Name => "ExtraSlots";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Slots";
        public override string Theme => "Inventory";
        public override string Hint => "Extra inventory rows and dedicated equipment slots";

        internal static ExtraSlotsModule Inst;

        private ConfigEntry<bool> _equipmentSlots;
        private ConfigEntry<int> _utilitySlots;
        private ConfigEntry<int> _foodSlots;
        private ConfigEntry<int> _ammoSlots;
        private ConfigEntry<int> _quickSlots;
        private ConfigEntry<int> _genericSlots;
        private ConfigEntry<bool> _autoEat;
        private ConfigEntry<bool> _showUi;
        private ConfigEntry<string> _quickKeys;
        private ConfigEntry<float> _panelOffsetX;
        private ConfigEntry<float> _panelOffsetY;
        private ConfigEntry<float> _panelScale;

        private static KeyCode[] _keys = new KeyCode[0];
        private static readonly ItemDrop.ItemData[] ExtraUtility = new ItemDrop.ItemData[3];
        private static bool _syncing;
        private static float _eatTimer;
        private static int _tickErrors;
        private static bool _commandRegistered;

        private static readonly MethodInfo SetupEquipmentMi = AccessTools.Method(typeof(Humanoid), "SetupEquipment");
        private static readonly FieldInfo EquipSeFi = AccessTools.Field(typeof(Humanoid), "m_equipmentStatusEffects");
        private static readonly FieldInfo SemanFi = AccessTools.Field(typeof(Character), "m_seman");

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _equipmentSlots = BindSynced("EquipmentSlots", true,
                "Server: give every player four dedicated equipment slots (head, chest, legs, cape). " +
                "Off removes the four slots; anything in them is moved back into the bag first.",
                Opt.B("Give every player four dedicated equipment slots"));
            _utilitySlots = BindSynced("UtilitySlots", 2,
                "Server: how many utility slots (0-4). 2 lets a player wear Megingjord and the " +
                "Wishbone at the same time. 0 disables the group.",
                Opt.N("How many extra utility (belt-type) slots", 0, 4));
            _foodSlots = BindSynced("FoodSlots", 3,
                "Server: how many food slots (0-3). Only food goes in them.",
                Opt.N("How many dedicated food slots", 0, 3));
            _ammoSlots = BindSynced("AmmoSlots", 2,
                "Server: how many ammo slots (0-4). The equipped ammo stack lives here.",
                Opt.N("How many dedicated ammo slots", 0, 4));
            _quickSlots = BindSynced("QuickSlots", 0,
                "Server: how many quick slots (0-8). Anything can go in them; a hotkey uses it. " +
                "0 by default since 0.4.2 - the bottom row is GenericSlots plain storage instead. " +
                "Set it above 0 to bring the hotkey row back; quick slots are drawn first, then " +
                "the generic ones, on the same row.",
                Opt.N("How many hotkeyed quick-use slots", 0, 8));
            _genericSlots = BindSynced("GenericSlots", 2,
                "Server: how many plain storage slots (0-8) on the bottom row. Any item fits, " +
                "there is no hotkey and nothing is drawn on the cell - they are simply two more " +
                "places to put things.",
                Opt.N("How many plain extra storage slots", 0, 8));
            _autoEat = BindSynced("AutoEatFromFoodSlots", true,
                "Server: when a food buff runs out and the same food is sitting in a food slot, " +
                "eat it automatically.",
                Opt.B("Automatically eat from a food slot when a buff runs out"));

            _quickKeys = BindLocal("QuickSlotKeys", "Z,X,C",
                "Local: comma-separated keys for the quick slots, in order. Unity KeyCode names " +
                "(Z, X, C, F1, Keypad1 ...). Use None to leave a quick slot without a hotkey. " +
                "Never synced, so each player picks their own.",
                Opt.T("Which keys trigger each quick slot, in order"));
            _showUi = BindLocal("ShowUI", true,
                "Local: draw the extra slots in their own panel beside the inventory window. " +
                "Turn off if a game update breaks the layout - the items stay exactly where they " +
                "are and stay reachable, they just fall back to plain extra rows under the bag.",
                Opt.B("Show the extra slots in their own panel"));
            _panelOffsetX = BindLocal("PanelOffsetX", 0f,
                "Local: nudge the extra-slot panel right (negative moves it left, towards the " +
                "inventory window). Pixels at 100% UI scale.",
                Opt.N("How far to shift the extra-slot panel sideways", -500, 500));
            _panelOffsetY = BindLocal("PanelOffsetY", 0f,
                "Local: nudge the extra-slot panel down. Pixels at 100% UI scale.",
                Opt.N("How far to shift the extra-slot panel up or down", -500, 500));
            _panelScale = BindLocal("PanelScale", 1f,
                "Local: size of the extra-slot panel relative to the inventory grid (0.4-2.5). " +
                "1 draws the slots exactly the size of the bag's own slots.",
                Opt.N("Size of the extra-slot panel relative to the inventory grid", 0.4, 2.5, 0.05));

            ParseKeys();
            RebuildLayout();
        }

        private void ParseKeys()
        {
            var parts = (_quickKeys.Value ?? "").Split(',');
            var keys = new List<KeyCode>();
            for (int i = 0; i < parts.Length; i++)
            {
                var s = parts[i].Trim();
                if (s.Length == 0) { keys.Add(KeyCode.None); continue; }
                try { keys.Add((KeyCode)Enum.Parse(typeof(KeyCode), s, true)); }
                catch
                {
                    Log.LogWarning("[Slots] QuickSlotKeys: '" + s + "' is not a Unity KeyCode - that slot has no hotkey");
                    keys.Add(KeyCode.None);
                }
            }
            _keys = keys.ToArray();
        }

        private void RebuildLayout()
        {
            SlotLayout.Rebuild(_equipmentSlots.Value, _utilitySlots.Value, _foodSlots.Value,
                               _ammoSlots.Value, _quickSlots.Value, _genericSlots.Value);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ParseKeys();

            // [Slots] Enabled going false leaves the patches installed but every body inert, so
            // the save lift would stop running while items were still sitting in the extra rows -
            // and the next Player.Save would write them into the vanilla package out of bounds,
            // which is precisely the deletion this module exists to prevent. Evacuate first.
            if (ReferenceEquals(entry, EnabledCfg))
            {
                if (!Enabled) EvacuateAndShrink("module turned off");
                else if (SlotStore.Managed != null)
                {
                    SlotStore.SetHeight(SlotStore.Managed, SlotLayout.TotalHeight);
                    SlotStore.Changed(SlotStore.Managed);
                    Log.LogInfo("[Slots] module turned on, grid back to " + SlotLayout.TotalHeight + " rows");
                }
                SlotsUi.Invalidate();
                return;
            }

            var before = SlotLayout.Describe();
            Relayout();
            if (before != SlotLayout.Describe()) Log.LogInfo("[Slots] layout now " + SlotLayout.Describe());
            SlotsUi.Invalidate();
        }

        /// <summary>Get every extra-slot item back into the vanilla grid, then shrink it.</summary>
        private static void EvacuateAndShrink(string why)
        {
            var inv = SlotStore.Managed;
            if (inv == null) return;
            try
            {
                var stranded = SlotStore.ExtraItems(inv);
                SlotStore.Evacuate(Player.m_localPlayer, inv, stranded, why);
                SlotStore.SetHeight(inv, SlotLayout.VanillaHeight);
                SlotStore.Changed(inv);
                Log.LogWarning("[Slots] " + why + ": " + stranded.Count +
                               " item(s) evacuated, grid back to " + SlotLayout.VanillaHeight + " rows");
            }
            catch (Exception e) { Log.LogError("[Slots] evacuation (" + why + ") failed: " + e); }
        }

        /// <summary>Rebuild the layout and move every extra-slot item to where it belongs now.</summary>
        private void Relayout()
        {
            var inv = SlotStore.Managed;
            if (inv == null) { RebuildLayout(); return; }

            var player = Player.m_localPlayer;
            var entries = SlotStore.Collect(inv);
            var leftovers = SlotStore.Orphans(inv);

            var list = SlotStore.Items(inv);
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].m_gridPos.y >= SlotLayout.VanillaHeight) list.RemoveAt(i);

            RebuildLayout();
            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
            SlotStore.Inject(inv, entries, leftovers);
            SlotStore.Evacuate(player, inv, leftovers, "slot layout change");
            SlotStore.Changed(inv);
        }

        // ---- patches ----------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            Inst = this;
            var self = typeof(ExtraSlotsModule);

            var invSave = AccessTools.Method(typeof(Inventory), "Save", new[] { typeof(ZPackage) });
            var addItemLoad = AccessTools.Method(typeof(Inventory), "AddItem", new[]
            {
                typeof(string), typeof(int), typeof(float), typeof(Vector2i), typeof(bool), typeof(int),
                typeof(int), typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool)
            });
            var findEmpty = AccessTools.Method(typeof(Inventory), "FindEmptySlot", new[] { typeof(bool) });
            var emptySlots = AccessTools.Method(typeof(Inventory), "GetEmptySlots");
            var haveEmpty = AccessTools.Method(typeof(Inventory), "HaveEmptySlot");
            var pSave = AccessTools.Method(typeof(Player), "Save", new[] { typeof(ZPackage) });
            var pLoad = AccessTools.Method(typeof(Player), "Load", new[] { typeof(ZPackage) });
            var pSpawned = AccessTools.Method(typeof(Player), "OnSpawned", new[] { typeof(bool) });
            var pUpdate = AccessTools.Method(typeof(Player), "Update");
            var pInvChanged = AccessTools.Method(typeof(Player), "OnInventoryChanged");
            var gridDrop = AccessTools.Method(typeof(InventoryGrid), "DropItem",
                new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(Vector2i) });
            var updSe = AccessTools.Method(typeof(Humanoid), "UpdateEquipmentStatusEffects");
            var isEquiped = AccessTools.Method(typeof(Humanoid), "IsItemEquiped", new[] { typeof(ItemDrop.ItemData) });
            var unequip = AccessTools.Method(typeof(Humanoid), "UnequipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) });
            var unequipAll = AccessTools.Method(typeof(Humanoid), "UnequipAllItems");
            var containerAwake = AccessTools.Method(typeof(Container), "Awake");
            var termInit = AccessTools.Method(typeof(Terminal), "InitTerminal");

            Require(invSave, "Inventory.Save(ZPackage)");
            Require(addItemLoad, "Inventory.AddItem(string,int,float,Vector2i,bool,int,int,long,string,Dictionary,int,bool)");
            Require(findEmpty, "Inventory.FindEmptySlot(bool)");
            Require(emptySlots, "Inventory.GetEmptySlots()");
            Require(haveEmpty, "Inventory.HaveEmptySlot()");
            Require(pSave, "Player.Save(ZPackage)");
            Require(pLoad, "Player.Load(ZPackage)");
            Require(pSpawned, "Player.OnSpawned(bool)");
            Require(pUpdate, "Player.Update()");
            Require(pInvChanged, "Player.OnInventoryChanged()");
            Require(gridDrop, "InventoryGrid.DropItem(Inventory,ItemData,int,Vector2i)");
            Require(updSe, "Humanoid.UpdateEquipmentStatusEffects()");
            Require(isEquiped, "Humanoid.IsItemEquiped(ItemData)");
            Require(unequip, "Humanoid.UnequipItem(ItemData,bool)");
            Require(unequipAll, "Humanoid.UnequipAllItems()");
            Require(containerAwake, "Container.Awake()");
            Require(termInit, "Terminal.InitTerminal()");
            if (SetupEquipmentMi == null) throw new Exception("Humanoid.SetupEquipment() not found");
            if (EquipSeFi == null) throw new Exception("Humanoid.m_equipmentStatusEffects not found");
            if (SemanFi == null) throw new Exception("Character.m_seman not found");

            Harmony.Patch(invSave,
                prefix: new HarmonyMethod(self, nameof(InvSavePrefix)),
                postfix: new HarmonyMethod(self, nameof(InvSavePostfix)));
            Harmony.Patch(addItemLoad, prefix: new HarmonyMethod(typeof(SlotsRescue), nameof(SlotsRescue.AddItemLoadPrefix)));
            Harmony.Patch(findEmpty, prefix: new HarmonyMethod(self, nameof(FindEmptySlotPrefix)));
            Harmony.Patch(emptySlots, postfix: new HarmonyMethod(self, nameof(GetEmptySlotsPostfix)));
            Harmony.Patch(haveEmpty, postfix: new HarmonyMethod(self, nameof(HaveEmptySlotPostfix)));
            Harmony.Patch(pSave, prefix: new HarmonyMethod(self, nameof(PlayerSavePrefix)));
            Harmony.Patch(pLoad,
                prefix: new HarmonyMethod(self, nameof(PlayerLoadPrefix)),
                postfix: new HarmonyMethod(self, nameof(PlayerLoadPostfix)));
            Harmony.Patch(pSpawned, postfix: new HarmonyMethod(self, nameof(PlayerSpawnedPostfix)));
            Harmony.Patch(pUpdate, postfix: new HarmonyMethod(self, nameof(PlayerUpdatePostfix)));
            Harmony.Patch(pInvChanged, postfix: new HarmonyMethod(self, nameof(InventoryChangedPostfix)));
            Harmony.Patch(gridDrop, prefix: new HarmonyMethod(self, nameof(GridDropPrefix)));
            Harmony.Patch(updSe, postfix: new HarmonyMethod(self, nameof(UpdateEquipSePostfix)));
            Harmony.Patch(isEquiped, postfix: new HarmonyMethod(self, nameof(IsItemEquipedPostfix)));
            Harmony.Patch(unequip, prefix: new HarmonyMethod(self, nameof(UnequipItemPrefix)));
            Harmony.Patch(unequipAll, postfix: new HarmonyMethod(self, nameof(UnequipAllPostfix)));
            Harmony.Patch(containerAwake, postfix: new HarmonyMethod(self, nameof(ContainerAwakePostfix)));
            Harmony.Patch(termInit, postfix: new HarmonyMethod(self, nameof(RegisterCommand)));

            SlotsUi.Install(Harmony,
                () => Inst != null && Inst.Active && Inst._showUi.Value,
                () => Inst == null ? Vector2.zero
                                   : new Vector2(Inst._panelOffsetX.Value, Inst._panelOffsetY.Value),
                () => Inst == null ? 1f : Inst._panelScale.Value,
                QuickKeyLabel);
        }

        /// <summary>
        /// What to print on quick slot <paramref name="index"/> (0-based): its own hotkey, so the
        /// panel reads "Z X C" out of the box and follows [Slots] QuickSlotKeys if it is changed.
        /// </summary>
        private static string QuickKeyLabel(int index)
        {
            if (index < 0 || index >= _keys.Length) return "";
            var key = _keys[index];
            return key == KeyCode.None ? "" : key.ToString();
        }

        private static void Require(MethodBase m, string what)
        {
            if (m == null) throw new Exception(what + " not found");
        }

        /// <summary>
        /// Only ever reached from a failed ApplyPatches (nothing is set up yet) or from the
        /// plugin's OnDestroy at application quit. Deliberately does NOT evacuate: at quit the
        /// profile has already been written and dropping items into a world that is shutting down
        /// would lose them. Turning [Slots] Enabled off at runtime is the path that evacuates -
        /// see OnConfigChanged.
        /// </summary>
        public override void Disable()
        {
            SlotStore.Managed = null;
            base.Disable();
        }

        private static bool Live()
        {
            return Inst != null && Inst.Active && ClientActive();
        }

        private static bool IsManaged(Inventory inv)
        {
            return inv != null && ReferenceEquals(inv, SlotStore.Managed);
        }

        // ---- persistence ------------------------------------------------------------------------

        /// <summary>Fresh blob into custom data, before vanilla Player.Save serialises it.</summary>
        private static void PlayerSavePrefix(Player __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                if (!IsManaged(__instance.GetInventory())) return;
                SlotStore.WriteBlob(__instance.m_customData, __instance.GetInventory());
            }
            catch (Exception e) { Log.LogError("[Slots] writing the save blob failed: " + e); }
        }

        private static void InvSavePrefix(Inventory __instance)
        {
            if (!Live() || !IsManaged(__instance)) return;
            try { SlotStore.Stash(__instance); }
            catch (Exception e) { Log.LogError("[Slots] stash failed: " + e); }
        }

        private static void InvSavePostfix(Inventory __instance)
        {
            if (!Live() || !IsManaged(__instance)) return;
            try { SlotStore.Unstash(__instance); }
            catch (Exception e) { Log.LogError("[Slots] unstash failed - items may be missing until relog: " + e); }
        }

        /// <summary>
        /// Shrink the grid back to vanilla and arm the migration capture, so that every item the
        /// package holds beyond row 3 is caught instead of deleted (shudnal's ExtraSlots put them
        /// there; ours never does).
        /// </summary>
        private static void PlayerLoadPrefix(Player __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                var inv = __instance.GetInventory();
                SlotStore.Managed = inv;
                SlotStore.SetHeight(inv, SlotLayout.VanillaHeight);
                SlotsRescue.BeginCapture(inv);
            }
            catch (Exception e) { Log.LogError("[Slots] load prefix failed: " + e); }
        }

        private static void PlayerLoadPostfix(Player __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                SlotsRescue.EndCapture();
                var inv = __instance.GetInventory();
                SlotStore.Managed = inv;
                SlotStore.SetHeight(inv, SlotLayout.TotalHeight);

                var entries = SlotStore.ReadBlob(__instance.m_customData, 0);
                var leftovers = new List<ItemDrop.ItemData>();
                int placed = SlotStore.Inject(inv, entries, leftovers);
                if (entries != null)
                    Log.LogInfo("[Slots] loaded " + placed + "/" + entries.Count + " item(s) from " +
                                SlotStore.BlobKey + " (" + SlotLayout.Describe() + ")");
                SlotStore.Evacuate(__instance, inv, leftovers, "slot no longer exists");

                SlotsRescue.Place(__instance, inv);
                SlotStore.Changed(inv);
                SlotsUi.Invalidate();
            }
            catch (Exception e) { Log.LogError("[Slots] load postfix failed: " + e); }
        }

        private static void PlayerSpawnedPostfix(Player __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                var inv = __instance.GetInventory();
                SlotStore.Managed = inv;
                SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
                var orphans = SlotStore.Orphans(inv);
                if (orphans.Count > 0) SlotStore.Evacuate(__instance, inv, orphans, "no slot at that cell");
                SlotStore.Changed(inv);
                SlotsUi.Invalidate();
            }
            catch (Exception e) { Log.LogError("[Slots] spawn setup failed: " + e); }
        }

        // ---- keeping ordinary items out of the extra area ------------------------------------------

        private static bool FindEmptySlotPrefix(Inventory __instance, bool topFirst, ref Vector2i __result)
        {
            if (!Live() || !IsManaged(__instance)) return true;
            int w = __instance.GetWidth();
            if (topFirst)
            {
                for (int y = 0; y < SlotLayout.VanillaHeight; y++)
                    for (int x = 0; x < w; x++)
                        if (__instance.GetItemAt(x, y) == null) { __result = new Vector2i(x, y); return false; }
            }
            else
            {
                for (int y = SlotLayout.VanillaHeight - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++)
                        if (__instance.GetItemAt(x, y) == null) { __result = new Vector2i(x, y); return false; }
            }
            __result = new Vector2i(-1, -1);
            return false;
        }

        private static int VanillaAreaUsed(Inventory inv)
        {
            int n = 0;
            var list = SlotStore.Items(inv);
            for (int i = 0; i < list.Count; i++)
                if (list[i].m_gridPos.y < SlotLayout.VanillaHeight) n++;
            return n;
        }

        private static void GetEmptySlotsPostfix(Inventory __instance, ref int __result)
        {
            if (!Live() || !IsManaged(__instance)) return;
            __result = SlotLayout.VanillaWidth * SlotLayout.VanillaHeight - VanillaAreaUsed(__instance);
        }

        private static void HaveEmptySlotPostfix(Inventory __instance, ref bool __result)
        {
            if (!Live() || !IsManaged(__instance)) return;
            __result = VanillaAreaUsed(__instance) < SlotLayout.VanillaWidth * SlotLayout.VanillaHeight;
        }

        /// <summary>Only an item a slot accepts may be dropped into it.</summary>
        private static bool GridDropPrefix(InventoryGrid __instance, ItemDrop.ItemData item,
                                           Vector2i pos, ref bool __result)
        {
            if (!Live() || item == null) return true;
            var inv = __instance.GetInventory();
            if (!IsManaged(inv) || !SlotLayout.IsExtra(pos)) return true;

            var slot = SlotLayout.At(pos);
            if (slot != null && SlotLayout.Accepts(slot, item)) return true;

            var p = Player.m_localPlayer;
            if (p != null)
                p.Message(MessageHud.MessageType.Center,
                    slot == null ? "This slot is not in use" : ("Only " + slot.Label + " items fit here"));
            __result = false;
            return false;
        }

        // ---- equipment sync ---------------------------------------------------------------------

        private static void InventoryChangedPostfix(Player __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            SyncEquipment(__instance);
        }

        /// <summary>
        /// Wear whatever sits in an equipment slot. The items are ordinary members of the player's
        /// inventory, so the four armour slots and the FIRST utility slot go straight through
        /// vanilla Humanoid.EquipItem. Utility slots 2+ have no vanilla field to live in
        /// (Humanoid.m_utilityItem is a single reference), so they are held here and their equip
        /// status effect is re-applied in UpdateEquipSePostfix.
        /// </summary>
        internal static void SyncEquipment(Player p)
        {
            if (_syncing || p == null) return;
            _syncing = true;
            try
            {
                var inv = p.GetInventory();
                if (!IsManaged(inv)) return;
                bool extraChanged = false;

                for (int i = 0; i < ExtraUtility.Length; i++)
                {
                    var held = ExtraUtility[i];
                    if (held == null) continue;
                    var slot = SlotLayout.ByKey("utility" + (i + 2));
                    if (slot == null || !ReferenceEquals(inv.GetItemAt(slot.Pos.x, slot.Pos.y), held))
                    {
                        held.m_equipped = false;
                        ExtraUtility[i] = null;
                        extraChanged = true;
                    }
                }

                var slots = SlotLayout.Slots;
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (!SlotLayout.IsEquipmentKind(s.Kind)) continue;
                    var item = inv.GetItemAt(s.Pos.x, s.Pos.y);
                    if (item == null) continue;

                    if (s.Kind == SlotKind.Utility && s.Index >= 2)
                    {
                        int idx = s.Index - 2;
                        if (idx >= ExtraUtility.Length) continue;
                        if (!ReferenceEquals(ExtraUtility[idx], item))
                        {
                            ExtraUtility[idx] = item;
                            item.m_equipped = true;
                            extraChanged = true;
                        }
                        continue;
                    }

                    if (!p.IsItemEquiped(item)) p.EquipItem(item, false);
                }

                if (extraChanged && SetupEquipmentMi != null) SetupEquipmentMi.Invoke(p, null);
            }
            catch (Exception e) { Log.LogError("[Slots] equipment sync failed: " + e); }
            finally { _syncing = false; }
        }

        /// <summary>Vanilla rebuilt the equipment status effects and does not know about utility 2+.</summary>
        private static void UpdateEquipSePostfix(Humanoid __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                var set = EquipSeFi.GetValue(__instance) as HashSet<StatusEffect>;
                var seman = SemanFi.GetValue(__instance) as SEMan;
                if (set == null || seman == null) return;
                for (int i = 0; i < ExtraUtility.Length; i++)
                {
                    var it = ExtraUtility[i];
                    if (it == null || it.m_shared == null) continue;
                    var se = it.m_shared.m_equipStatusEffect;
                    if (se == null || set.Contains(se)) continue;
                    seman.AddStatusEffect(se);
                    set.Add(se);
                }
            }
            catch (Exception e) { Log.LogError("[Slots] extra utility status effects failed: " + e); }
        }

        private static void IsItemEquipedPostfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (__result || item == null || !Live()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            for (int i = 0; i < ExtraUtility.Length; i++)
                if (ReferenceEquals(ExtraUtility[i], item)) { __result = true; return; }
        }

        private static void UnequipItemPrefix(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (item == null || !Live()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            for (int i = 0; i < ExtraUtility.Length; i++)
            {
                if (!ReferenceEquals(ExtraUtility[i], item)) continue;
                ExtraUtility[i] = null;
                item.m_equipped = false;
            }
        }

        private static void UnequipAllPostfix(Humanoid __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            for (int i = 0; i < ExtraUtility.Length; i++)
            {
                if (ExtraUtility[i] != null) ExtraUtility[i].m_equipped = false;
                ExtraUtility[i] = null;
            }
        }

        /// <summary>
        /// Death insurance. Inventory.MoveInventoryToGrave copies the player's grid HEIGHT onto the
        /// tombstone's inventory, but a reloaded tombstone gets its size from the prefab, so any
        /// item that sat in an extra row would hit the same out-of-bounds delete in Inventory.Load.
        /// Widening every tombstone container to the maximum extra height closes that.
        /// </summary>
        private static void ContainerAwakePostfix(Container __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                if (__instance.GetComponent<TombStone>() == null) return;
                var inv = __instance.GetInventory();
                if (inv == null) return;
                int want = SlotLayout.VanillaHeight + SlotLayout.MaxExtraRows;
                if (SlotStore.GetHeight(inv) < want) SlotStore.SetHeight(inv, want);
            }
            catch (Exception e) { Log.LogWarning("[Slots] tombstone widening failed: " + e.Message); }
        }

        // ---- input and auto-eat ---------------------------------------------------------------------

        private static void PlayerUpdatePostfix(Player __instance)
        {
            if (!Live() || __instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                var inv = __instance.GetInventory();
                if (!IsManaged(inv)) return;

                if (InputAllowed(__instance))
                {
                    for (int i = 0; i < SlotLayout.QuickCount && i < _keys.Length; i++)
                    {
                        var key = _keys[i];
                        if (key == KeyCode.None) continue;
                        if (!ZInput.GetKeyDown(key, false)) continue;
                        var slot = SlotLayout.ByKey("quick" + (i + 1));
                        if (slot == null) continue;
                        var item = inv.GetItemAt(slot.Pos.x, slot.Pos.y);
                        if (item == null) continue;
                        __instance.UseItem(null, item, false);
                    }
                }

                if (Inst._autoEat.Value && SlotLayout.FoodCount > 0)
                {
                    _eatTimer += Time.deltaTime;
                    if (_eatTimer >= 1f)
                    {
                        _eatTimer = 0f;
                        AutoEat(__instance, inv);
                    }
                }
            }
            catch (Exception e)
            {
                if (_tickErrors++ < 3) Log.LogError("[Slots] update tick failed (" + _tickErrors + "/3): " + e);
            }
        }

        private static void AutoEat(Player p, Inventory inv)
        {
            if (p.IsDead() || p.InCutscene()) return;
            for (int i = 1; i <= SlotLayout.FoodCount; i++)
            {
                var slot = SlotLayout.ByKey("food" + i);
                if (slot == null) continue;
                var item = inv.GetItemAt(slot.Pos.x, slot.Pos.y);
                if (item == null || !SlotLayout.IsFood(item)) continue;
                if (!p.CanEat(item, false)) continue;
                p.UseItem(null, item, false);
                return;   // one bite per tick, exactly like a player pressing the key
            }
        }

        private static bool InputAllowed(Player me)
        {
            if (!me.TakeInput()) return false;
            if (Hud.InRadial() || Hud.IsPieceSelectionVisible()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            if (Console.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || StoreGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen()) return false;
            return true;
        }

        // ---- console command -------------------------------------------------------------------------

        private static void RegisterCommand()
        {
            if (_commandRegistered) return;
            _commandRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("nvlb.slots.restore",
                    "Re-inject the extra-slot items from a backup: nvlb.slots.restore [1|2|3]",
                    new Terminal.ConsoleEvent(RestoreCommand));
                Log.LogInfo("[Slots] console command 'nvlb.slots.restore' registered");
            }
            catch (Exception e)
            {
                _commandRegistered = false;
                Log.LogError("[Slots] could not register nvlb.slots.restore: " + e);
            }
        }

        private static void RestoreCommand(Terminal.ConsoleEventArgs args)
        {
            var p = Player.m_localPlayer;
            if (p == null) { Say(args, "no local player"); return; }
            int which = 1;
            if (args.Length > 1 && !int.TryParse(args[1], out which)) which = 1;
            which = Mathf.Clamp(which, 0, SlotStore.Backups);

            var entries = SlotStore.ReadBlob(p.m_customData, which);
            if (entries == null || entries.Count == 0)
            {
                Say(args, "backup " + which + " is empty or unreadable" +
                          " (keys: " + SlotStore.BlobKey + ", " + SlotStore.BackupKey(1) + "..." +
                          SlotStore.BackupKey(SlotStore.Backups) + ")");
                return;
            }

            var inv = p.GetInventory();
            var leftovers = new List<ItemDrop.ItemData>();
            int placed = SlotStore.Inject(inv, entries, leftovers);
            SlotStore.Evacuate(p, inv, leftovers, "nvlb.slots.restore");
            SlotStore.Changed(inv);
            Say(args, "restored " + placed + "/" + entries.Count + " item(s) from backup " + which);
        }

        private static void Say(Terminal.ConsoleEventArgs args, string s)
        {
            if (args.Context != null) args.Context.AddString("[Slots] " + s);
            Log.LogInfo("[Slots] " + s);
        }

        // ---- status -----------------------------------------------------------------------------------

        public override string StatusDetail()
        {
            var s = SlotLayout.Describe() + " autoEat=" + (_autoEat != null && _autoEat.Value) +
                    " ui=" + (_showUi != null && _showUi.Value) +
                    (SlotLayout.QuickCount > 0 ? " keys=" + (_quickKeys != null ? _quickKeys.Value : "") : "");
            if (SlotStore.Managed != null)
                s += " live=" + SlotStore.ExtraItems(SlotStore.Managed).Count + " item(s)";
            return s;
        }
    }
}
