using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The data-safety layer. Everything that touches the player's Inventory list or the side blob
    /// goes through here, so there is exactly one place to audit against QOL-ARCHITECTURE section 4.
    ///
    ///   rule 1  extra-slot items are lifted out of the list before Inventory.Save writes the
    ///           vanilla package and put back straight afterwards  -> Stash / Unstash
    ///   rule 2  they live in Player.m_customData["nvlb.slots"]     -> WriteBlob / ReadBlob
    ///   rule 3  shrinking the layout or disabling the module evacuates instead of dropping items
    ///           -> Evacuate, which tries free vanilla cells first and the player's feet last, and
    ///           logs every single item it moves
    ///   rule 4  three rolling backups, nvlb.slots.bak1..3, plus the nvlb.slots.restore command
    ///   rule 5  the slot counts are BindSynced, so every client on a character agrees on the grid
    /// </summary>
    internal static class SlotStore
    {
        public const string BlobKey = "nvlb.slots";
        public const int Backups = 3;

        private static readonly AccessTools.FieldRef<Inventory, List<ItemDrop.ItemData>> ItemsRef =
            AccessTools.FieldRefAccess<Inventory, List<ItemDrop.ItemData>>("m_inventory");
        private static readonly AccessTools.FieldRef<Inventory, int> HeightRef =
            AccessTools.FieldRefAccess<Inventory, int>("m_height");
        private static readonly MethodInfo ChangedMi = AccessTools.Method(typeof(Inventory), "Changed");

        /// <summary>The one inventory this module manages: the local player's. Null when not in a game.</summary>
        public static Inventory Managed;

        private static readonly List<ItemDrop.ItemData> _stashed = new List<ItemDrop.ItemData>();
        private static int _stashDepth;

        public static string BackupKey(int n) { return BlobKey + ".bak" + n; }

        /// <summary>True once the storage layer has been wired to a real inventory.</summary>
        public static bool Ready { get { return Managed != null; } }

        // ---- low level ---------------------------------------------------------------------

        public static List<ItemDrop.ItemData> Items(Inventory inv) { return ItemsRef(inv); }

        public static void Changed(Inventory inv)
        {
            if (ChangedMi != null) ChangedMi.Invoke(inv, null);
        }

        public static int GetHeight(Inventory inv) { return HeightRef(inv); }

        public static void SetHeight(Inventory inv, int h) { HeightRef(inv) = h; }

        /// <summary>
        /// Put an item at an exact grid cell without going through Inventory.AddItem. The vanilla
        /// entry points either clone the item (losing identity) or try to merge it into an
        /// existing stack anywhere in the inventory, which would silently pull an arrow stack out
        /// of ammo slot 2 into ammo slot 1. Placement here is exact or it fails.
        /// </summary>
        public static bool PlaceRaw(Inventory inv, ItemDrop.ItemData item, Vector2i pos)
        {
            if (inv == null || item == null) return false;
            if (pos.x < 0 || pos.y < 0 || pos.x >= inv.GetWidth() || pos.y >= GetHeight(inv)) return false;
            if (inv.GetItemAt(pos.x, pos.y) != null) return false;
            item.m_gridPos = pos;
            Items(inv).Add(item);
            return true;
        }

        /// <summary>First empty cell inside the VANILLA area only (never an extra slot).</summary>
        public static Vector2i FindFreeVanillaCell(Inventory inv)
        {
            for (int y = 0; y < SlotLayout.VanillaHeight; y++)
                for (int x = 0; x < SlotLayout.VanillaWidth; x++)
                    if (inv.GetItemAt(x, y) == null) return new Vector2i(x, y);
            return new Vector2i(-1, -1);
        }

        /// <summary>Every item currently sitting in the extra area (y >= vanilla height).</summary>
        public static List<ItemDrop.ItemData> ExtraItems(Inventory inv)
        {
            var outp = new List<ItemDrop.ItemData>();
            if (inv == null) return outp;
            var list = Items(inv);
            for (int i = 0; i < list.Count; i++)
                if (list[i].m_gridPos.y >= SlotLayout.VanillaHeight) outp.Add(list[i]);
            return outp;
        }

        // ---- stash / unstash (the Save lift) ------------------------------------------------

        /// <summary>
        /// Take every extra-slot item out of the inventory list. Re-entrant: nested calls are
        /// counted so an Inventory.Save nested inside a Player.Save cannot unstash early.
        /// </summary>
        public static void Stash(Inventory inv)
        {
            if (inv == null) return;
            if (_stashDepth++ > 0) return;
            _stashed.Clear();
            var list = Items(inv);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].m_gridPos.y < SlotLayout.VanillaHeight) continue;
                _stashed.Add(list[i]);
                list.RemoveAt(i);
            }
        }

        /// <summary>Put the stashed items back exactly where they were.</summary>
        public static void Unstash(Inventory inv)
        {
            if (inv == null) return;
            if (--_stashDepth > 0) return;
            _stashDepth = 0;
            if (_stashed.Count == 0) return;
            var list = Items(inv);
            for (int i = _stashed.Count - 1; i >= 0; i--) list.Add(_stashed[i]);
            _stashed.Clear();
        }

        /// <summary>How many items the last Stash lifted out - used by the self test.</summary>
        public static int StashedCount { get { return _stashed.Count; } }

        // ---- blob ---------------------------------------------------------------------------

        /// <summary>Collect the current extra-slot contents as blob entries.</summary>
        public static List<SlotEntry> Collect(Inventory inv)
        {
            var entries = new List<SlotEntry>();
            if (inv == null) return entries;
            var extras = ExtraItems(inv);
            for (int i = 0; i < extras.Count; i++)
            {
                var slot = SlotLayout.At(extras[i].m_gridPos);
                if (slot == null) continue;   // padding cell; Evacuate deals with it
                entries.Add(new SlotEntry { SlotKey = slot.Key, Item = extras[i] });
            }
            return entries;
        }

        /// <summary>
        /// Write the blob into custom data and rotate the backups. Called from the Player.Save
        /// prefix, i.e. immediately before vanilla serialises m_customData.
        /// </summary>
        public static void WriteBlob(Dictionary<string, string> data, Inventory inv)
        {
            if (data == null) return;
            var entries = Collect(inv);
            var blob = SlotBlob.Encode(entries);

            string previous;
            data.TryGetValue(BlobKey, out previous);
            if (!string.IsNullOrEmpty(previous) && previous != blob)
            {
                for (int n = Backups; n > 1; n--)
                {
                    string older;
                    if (data.TryGetValue(BackupKey(n - 1), out older)) data[BackupKey(n)] = older;
                }
                data[BackupKey(1)] = previous;
            }

            if (entries.Count == 0 && string.IsNullOrEmpty(previous)) data.Remove(BlobKey);
            else data[BlobKey] = blob;
        }

        /// <summary>Read a blob (the live one, or backup 1..3) out of custom data.</summary>
        public static List<SlotEntry> ReadBlob(Dictionary<string, string> data, int backup)
        {
            if (data == null) return null;
            string blob;
            if (!data.TryGetValue(backup <= 0 ? BlobKey : BackupKey(backup), out blob)) return null;
            return SlotBlob.Decode(blob);
        }

        // ---- injection ------------------------------------------------------------------------

        /// <summary>
        /// Put decoded entries back into the live grid. Anything whose slot no longer exists, or
        /// whose slot is taken, comes back in <paramref name="leftovers"/> for Evacuate to handle.
        /// </summary>
        public static int Inject(Inventory inv, List<SlotEntry> entries, List<ItemDrop.ItemData> leftovers)
        {
            int placed = 0;
            if (inv == null || entries == null) return 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var slot = SlotLayout.ByKey(e.SlotKey);
                if (slot != null && PlaceRaw(inv, e.Item, slot.Pos)) { placed++; continue; }

                // Slot gone (config shrank) or occupied: try any other slot that fits, then bail out.
                var alt = SlotLayout.FindFreeFor(inv, e.Item);
                if (alt != null && PlaceRaw(inv, e.Item, alt.Pos))
                {
                    placed++;
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] " + SlotBlob.Describe(e.Item) +
                        " could not go back into slot '" + e.SlotKey + "' - moved to '" + alt.Key + "'");
                    continue;
                }
                if (leftovers != null) leftovers.Add(e.Item);
            }
            if (placed > 0) Changed(inv);
            return placed;
        }

        // ---- evacuation -----------------------------------------------------------------------

        /// <summary>
        /// Get items out of harm's way: free vanilla cell first, the player's feet last. Every
        /// item is logged. <paramref name="items"/> may or may not currently be in the inventory;
        /// anything still in it is removed first.
        /// </summary>
        public static void Evacuate(Player player, Inventory inv, List<ItemDrop.ItemData> items, string why)
        {
            if (items == null || items.Count == 0) return;
            int toBag = 0, toGround = 0;
            var list = inv != null ? Items(inv) : null;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (list != null) list.Remove(item);

                Vector2i free = inv != null ? FindFreeVanillaCell(inv) : new Vector2i(-1, -1);
                if (free.x >= 0 && PlaceRaw(inv, item, free))
                {
                    toBag++;
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] evacuate (" + why + "): " +
                        SlotBlob.Describe(item) + " -> inventory cell " + free.x + "," + free.y);
                    continue;
                }

                if (player != null && SlotBlob.PrefabNameOf(item) != null)
                {
                    try
                    {
                        ItemDrop.DropItem(item, item.m_stack,
                            player.transform.position + player.transform.forward * 0.6f + Vector3.up * 0.5f,
                            Quaternion.identity);
                        toGround++;
                        NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] evacuate (" + why + "): " +
                            SlotBlob.Describe(item) + " -> DROPPED at the player's feet");
                        continue;
                    }
                    catch (Exception e)
                    {
                        NoVikingLeftBehindPlugin.Log.LogError("[Slots] evacuate: could not drop " +
                            SlotBlob.Describe(item) + ": " + e.Message);
                    }
                }

                NoVikingLeftBehindPlugin.Log.LogError("[Slots] evacuate (" + why + "): NOWHERE to put " +
                    SlotBlob.Describe(item) + " - it stays in the save blob, use nvlb.slots.restore");
            }

            if (inv != null) Changed(inv);
            if (player != null && (toBag + toGround) > 0)
                player.Message(MessageHud.MessageType.Center,
                    "Extra slots: " + toBag + " item(s) moved to your bag, " + toGround + " dropped at your feet");
        }

        /// <summary>Anything sitting in the extra area with no slot under it (padding, or a shrunk layout).</summary>
        public static List<ItemDrop.ItemData> Orphans(Inventory inv)
        {
            var outp = new List<ItemDrop.ItemData>();
            if (inv == null) return outp;
            var extras = ExtraItems(inv);
            for (int i = 0; i < extras.Count; i++)
                if (SlotLayout.At(extras[i].m_gridPos) == null) outp.Add(extras[i]);
            return outp;
        }
    }
}
