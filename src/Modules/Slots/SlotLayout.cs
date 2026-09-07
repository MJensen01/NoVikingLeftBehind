using System;
using System.Collections.Generic;
using UnityEngine;

namespace NoVikingLeftBehind
{
    internal enum SlotKind
    {
        /// <summary>A cell in the extra area that no item may occupy (layout padding).</summary>
        Blocked,
        Helmet,
        Chest,
        Legs,
        Cape,
        Utility,
        Food,
        Ammo,
        Quick
    }

    /// <summary>One extra slot: where it is in the grid, what it accepts, what the UI labels it.</summary>
    internal sealed class SlotDef
    {
        public string Key;          // stable id used in the save blob: "helmet", "utility2", "quick1"
        public string Label;        // human name used in messages ("Only Helmet items fit here")
        public string PanelLabel;   // short text drawn in the side panel; "" means icon-only
        public SlotKind Kind;
        public int Index;           // 1-based within the kind
        public Vector2i Pos;        // absolute grid position in the EXTENDED player inventory

        /// <summary>
        /// Where the slot sits in the side panel, in QUARTER tiles from the panel's top-left
        /// corner, y growing downwards. Quarter tiles are shudnal's unit: they let a group be
        /// nudged half or quarter of a slot without any floating-point layout code.
        /// </summary>
        public Vector2 PanelTile;
    }

    /// <summary>
    /// The extra-slot grid layout.
    ///
    /// The player's vanilla inventory is 8 x 4 (Humanoid.cs:67 - `new Inventory("Inventory", null, 8, 4)`).
    /// ExtraSlots-style modules make the extra slots real cells of that same Inventory by growing
    /// its height, which is what makes every piece of vanilla machinery - drag and drop, tooltips,
    /// durability bars, Humanoid.EquipItem's `m_inventory.ContainsItem(item)` guard, weight - work
    /// with no extra code. InventoryGrid.UpdateGui rebuilds its cell grid straight from
    /// GetWidth()/GetHeight(), so the extra rows even get a cell each.
    ///
    /// The price of that choice is the data-loss vector documented in QOL-ARCHITECTURE section 4:
    /// Inventory.Load drops any item whose m_gridPos.y is past the grid height. THAT is why the
    /// items are never written into the vanilla package (see SlotStore / SlotBlob) - the grid is a
    /// runtime view, the blob in Player.m_customData is the truth.
    ///
    /// There are therefore TWO coordinate systems in this file and they are deliberately
    /// independent:
    ///
    ///   Pos       - the cell in the player's Inventory. Row-major over the extra rows, in the
    ///               order Helmet, Chest, Legs, Cape, Utility x U, Food x F, Ammo x A, Quick x Q.
    ///               This is storage. It is what SlotStore/SlotBlob/SlotsRescue see, and nothing
    ///               about it changed in 0.4.1.
    ///   PanelTile - where the cell is DRAWN, in the side panel that SlotsUi builds. This is the
    ///               shudnal-style layout: equipment in columns of three, a food column and an
    ///               ammo column beside them, quick slots on a row underneath.
    ///
    /// Keeping them apart is what lets the panel look like shudnal's without touching the save
    /// format: SlotsUi just moves each extra cell's RectTransform to its PanelTile.
    /// </summary>
    internal static class SlotLayout
    {
        public const int VanillaWidth = 8;
        public const int VanillaHeight = 4;

        /// <summary>Hard cap on extra rows. Also the height a tombstone container is widened to.</summary>
        public const int MaxExtraRows = 4;

        private static readonly List<SlotDef> _slots = new List<SlotDef>();
        private static readonly Dictionary<string, SlotDef> _byKey = new Dictionary<string, SlotDef>();
        private static SlotDef[,] _grid = new SlotDef[VanillaWidth, MaxExtraRows];

        public static int Rows { get; private set; }
        public static int UtilityCount { get; private set; }
        public static int FoodCount { get; private set; }
        public static int AmmoCount { get; private set; }
        public static int QuickCount { get; private set; }
        public static bool EquipmentOn { get; private set; }

        /// <summary>Side-panel size in whole tiles (a tile is one slot plus its spacing).</summary>
        public static float PanelTilesWide { get; private set; }
        public static float PanelTilesHigh { get; private set; }

        public static int TotalHeight { get { return VanillaHeight + Rows; } }
        public static IList<SlotDef> Slots { get { return _slots; } }

        static SlotLayout()
        {
            Rebuild(true, 2, 3, 2, 3);
        }

        /// <summary>Recompute the layout from the (synced) slot counts. Idempotent.</summary>
        public static void Rebuild(bool equipment, int utility, int food, int ammo, int quick)
        {
            EquipmentOn = equipment;
            UtilityCount = Mathf.Clamp(utility, 0, 4);
            FoodCount = Mathf.Clamp(food, 0, 3);
            AmmoCount = Mathf.Clamp(ammo, 0, 4);
            QuickCount = Mathf.Clamp(quick, 0, 8);

            _slots.Clear();
            _byKey.Clear();

            if (equipment)
            {
                Add(SlotKind.Helmet, 1, "helmet", "Helmet", "Head");
                Add(SlotKind.Chest, 1, "chest", "Chest", "Chest");
                Add(SlotKind.Legs, 1, "legs", "Legs", "Legs");
                Add(SlotKind.Cape, 1, "cape", "Cape", "Back");
            }
            for (int i = 1; i <= UtilityCount; i++) Add(SlotKind.Utility, i, "utility" + i, "Utility", "Utility");
            // Food and ammo slots are drawn with the game's own fork / arrow icon instead of a
            // word, exactly as shudnal does - a label would only repeat what the icon says.
            for (int i = 1; i <= FoodCount; i++) Add(SlotKind.Food, i, "food" + i, "Food", "");
            for (int i = 1; i <= AmmoCount; i++) Add(SlotKind.Ammo, i, "ammo" + i, "Ammo", "");
            // Quick slots are labelled with their hotkey at draw time (SlotsUi asks the module).
            for (int i = 1; i <= QuickCount; i++) Add(SlotKind.Quick, i, "quick" + i, "Quick", "");

            int rows = (_slots.Count + VanillaWidth - 1) / VanillaWidth;
            if (rows > MaxExtraRows)
            {
                // Should be impossible with the clamps above; refuse to lose slots silently.
                rows = MaxExtraRows;
                if (_slots.Count > rows * VanillaWidth) _slots.RemoveRange(rows * VanillaWidth, _slots.Count - rows * VanillaWidth);
            }
            Rows = rows;

            _grid = new SlotDef[VanillaWidth, MaxExtraRows];
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                s.Pos = new Vector2i(i % VanillaWidth, VanillaHeight + i / VanillaWidth);
                _grid[s.Pos.x, s.Pos.y - VanillaHeight] = s;
                _byKey[s.Key] = s;
            }

            ComputePanel();
        }

        private static void Add(SlotKind kind, int index, string key, string label, string panelLabel)
        {
            _slots.Add(new SlotDef { Key = key, Label = label, PanelLabel = panelLabel, Kind = kind, Index = index });
        }

        /// <summary>
        /// The side-panel geometry, adapted from shudnal's EquipmentPanel.SlotPositions. Equipment
        /// (the four armour slots plus the utility slots) fills columns of three, top to bottom
        /// then left to right; a food column and an ammo column stand a quarter tile to their
        /// right; the quick slots sit on a row a quarter tile below everything.
        /// </summary>
        private static void ComputePanel()
        {
            var equip = new List<SlotDef>();
            var food = new List<SlotDef>();
            var ammo = new List<SlotDef>();
            var quick = new List<SlotDef>();
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                if (IsEquipmentKind(s.Kind)) equip.Add(s);
                else if (s.Kind == SlotKind.Food) food.Add(s);
                else if (s.Kind == SlotKind.Ammo) ammo.Add(s);
                else if (s.Kind == SlotKind.Quick) quick.Add(s);
            }

            int lastColumn = equip.Count > 0 ? (equip.Count - 1) / 3 : -1;
            int equipHeight = (equip.Count > 3 || food.Count > 0 || ammo.Count > 0) ? 3 : equip.Count;

            // If there are more quick slots than equipment columns, centre the equipment over them.
            int equipShift = Math.Max(quick.Count - 1 - lastColumn, 0) * 2;
            for (int i = 0; i < equip.Count; i++)
                equip[i].PanelTile = new Vector2((i / 3) * 4 + equipShift, (i % 3) * 4);

            int sideColumn = Math.Max(lastColumn + 1, quick.Count);
            for (int i = 0; i < food.Count; i++)
                food[i].PanelTile = new Vector2(sideColumn * 4 + 1, i * 4);
            for (int i = 0; i < ammo.Count; i++)
                ammo[i].PanelTile = new Vector2(sideColumn * 4 + 1 + (food.Count > 0 ? 4 : 0), i * 4);

            // ...and the other way round, centre a short quick-slot row under the equipment.
            int quickShift = Math.Max(lastColumn + 1 - quick.Count, 0) * 2;
            for (int i = 0; i < quick.Count; i++)
                quick[i].PanelTile = new Vector2(i * 4 + quickShift, equipHeight * 4 + 1);

            float sideWidth = ((food.Count > 0 || ammo.Count > 0) ? 0.25f : 0f) +
                              (food.Count > 0 ? 1f : 0f) + (ammo.Count > 0 ? 1f : 0f);
            PanelTilesWide = Math.Max(quick.Count, lastColumn + 1) + sideWidth;
            PanelTilesHigh = (quick.Count > 0 ? 1.25f : 0f) + equipHeight;
        }

        /// <summary>The slot at an absolute grid position, or null (vanilla cell, or blocked padding).</summary>
        public static SlotDef At(int x, int y)
        {
            if (y < VanillaHeight || y >= TotalHeight) return null;
            if (x < 0 || x >= VanillaWidth) return null;
            return _grid[x, y - VanillaHeight];
        }

        public static SlotDef At(Vector2i p) { return At(p.x, p.y); }

        public static SlotDef ByKey(string key)
        {
            SlotDef s;
            return (key != null && _byKey.TryGetValue(key, out s)) ? s : null;
        }

        /// <summary>Is this position inside the extra area at all (slot or blocked padding)?</summary>
        public static bool IsExtra(Vector2i p)
        {
            return p.y >= VanillaHeight && p.y < TotalHeight && p.x >= 0 && p.x < VanillaWidth;
        }

        // ---- item eligibility -------------------------------------------------------------

        public static bool IsFood(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
            return item.m_shared.m_food > 0f || item.m_shared.m_foodStamina > 0f || item.m_shared.m_foodEitr > 0f;
        }

        /// <summary>May this item live in this slot?</summary>
        public static bool Accepts(SlotDef slot, ItemDrop.ItemData item)
        {
            if (slot == null || item == null || item.m_shared == null) return false;
            var t = item.m_shared.m_itemType;
            switch (slot.Kind)
            {
                case SlotKind.Helmet: return t == ItemDrop.ItemData.ItemType.Helmet;
                case SlotKind.Chest: return t == ItemDrop.ItemData.ItemType.Chest;
                case SlotKind.Legs: return t == ItemDrop.ItemData.ItemType.Legs;
                case SlotKind.Cape: return t == ItemDrop.ItemData.ItemType.Shoulder;
                case SlotKind.Utility: return t == ItemDrop.ItemData.ItemType.Utility;
                case SlotKind.Food: return IsFood(item);
                case SlotKind.Ammo:
                    return t == ItemDrop.ItemData.ItemType.Ammo || t == ItemDrop.ItemData.ItemType.AmmoNonEquipable;
                case SlotKind.Quick: return true;
                default: return false;   // Blocked
            }
        }

        /// <summary>True for the four armour slots plus utility - slots whose item should be worn.</summary>
        public static bool IsEquipmentKind(SlotKind k)
        {
            return k == SlotKind.Helmet || k == SlotKind.Chest || k == SlotKind.Legs ||
                   k == SlotKind.Cape || k == SlotKind.Utility;
        }

        /// <summary>First empty slot of any kind that would accept this item, or null.</summary>
        public static SlotDef FindFreeFor(Inventory inv, ItemDrop.ItemData item)
        {
            if (inv == null) return null;
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                if (!Accepts(s, item)) continue;
                if (inv.GetItemAt(s.Pos.x, s.Pos.y) == null) return s;
            }
            return null;
        }

        public static string Describe()
        {
            return "equipment=" + (EquipmentOn ? "on" : "off") +
                   " utility=" + UtilityCount + " food=" + FoodCount +
                   " ammo=" + AmmoCount + " quick=" + QuickCount +
                   " -> " + _slots.Count + " slots in " + Rows + " row(s)";
        }
    }
}
