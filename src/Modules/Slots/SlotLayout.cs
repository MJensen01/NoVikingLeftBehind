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
        public string Label;        // short UI label drawn in the cell's "binding" text
        public SlotKind Kind;
        public int Index;           // 1-based within the kind
        public Vector2i Pos;        // absolute grid position in the EXTENDED player inventory
    }

    /// <summary>
    /// The extra-slot grid layout.
    ///
    /// The player's vanilla inventory is 8 x 4 (Humanoid.cs:67 - `new Inventory("Inventory", null, 8, 4)`).
    /// ExtraSlots-style modules make the extra slots real cells of that same Inventory by growing
    /// its height, which is what makes every piece of vanilla machinery - drag and drop, tooltips,
    /// durability bars, Humanoid.EquipItem's `m_inventory.ContainsItem(item)` guard, weight - work
    /// with no extra code. InventoryGrid.UpdateGui rebuilds its cell grid straight from
    /// GetWidth()/GetHeight(), so the extra rows even draw themselves.
    ///
    /// The price of that choice is the data-loss vector documented in QOL-ARCHITECTURE section 4:
    /// Inventory.Load drops any item whose m_gridPos.y is past the grid height. THAT is why the
    /// items are never written into the vanilla package (see SlotStore / SlotBlob) - the grid is a
    /// runtime view, the blob in Player.m_customData is the truth.
    ///
    /// Layout is row-major over the extra rows, in this order:
    ///   Helmet, Chest, Legs, Cape, Utility x U, Food x F, Ammo x A, Quick x Q
    /// Leftover cells in the last row are Blocked and accept nothing.
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
                Add(SlotKind.Helmet, 1, "helmet", "Head");
                Add(SlotKind.Chest, 1, "chest", "Chest");
                Add(SlotKind.Legs, 1, "legs", "Legs");
                Add(SlotKind.Cape, 1, "cape", "Cape");
            }
            for (int i = 1; i <= UtilityCount; i++) Add(SlotKind.Utility, i, "utility" + i, "Util" + i);
            for (int i = 1; i <= FoodCount; i++) Add(SlotKind.Food, i, "food" + i, "Food" + i);
            for (int i = 1; i <= AmmoCount; i++) Add(SlotKind.Ammo, i, "ammo" + i, "Ammo" + i);
            for (int i = 1; i <= QuickCount; i++) Add(SlotKind.Quick, i, "quick" + i, "Q" + i);

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
        }

        private static void Add(SlotKind kind, int index, string key, string label)
        {
            _slots.Add(new SlotDef { Key = key, Label = label, Kind = kind, Index = index });
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
