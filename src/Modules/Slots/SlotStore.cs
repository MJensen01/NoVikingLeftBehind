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
        /// <summary>
        /// Valheim 1.0 turned <c>Inventory.Changed()</c> into
        /// <c>Changed(bool success = false, bool cheatedStateChanged = false)</c> (Inventory.cs:1104),
        /// and Invoke does not fill in defaults - a null argument array throws
        /// TargetParameterCountException. Both defaults are false and only gate the
        /// "picked up a cheated item" toast, so passing false for however many parameters the
        /// live build has is exactly vanilla's own <c>Changed()</c> call.
        /// </summary>
        private static readonly object[] ChangedArgs = ChangedArgsFor(ChangedMi);

        private static object[] ChangedArgsFor(MethodInfo mi)
        {
            if (mi == null) return null;
            var ps = mi.GetParameters();
            if (ps.Length == 0) return null;
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                                                : (ps[i].ParameterType.IsValueType
                                                   ? Activator.CreateInstance(ps[i].ParameterType) : null);
            return args;
        }

        /// <summary>The one inventory this module manages: the local player's. Null when not in a game.</summary>
        public static Inventory Managed;

        private static readonly List<ItemDrop.ItemData> _stashed = new List<ItemDrop.ItemData>();
        private static int _stashDepth;
        /// <summary>The inventory the open lift was taken from, so a reset can put them back there.</summary>
        private static Inventory _stashedFrom;

        public static string BackupKey(int n) { return BlobKey + ".bak" + n; }

        /// <summary>True once the storage layer has been wired to a real inventory.</summary>
        public static bool Ready { get { return Managed != null; } }

        // ---- low level ---------------------------------------------------------------------

        public static List<ItemDrop.ItemData> Items(Inventory inv) { return ItemsRef(inv); }

        public static void Changed(Inventory inv)
        {
            if (ChangedMi != null) ChangedMi.Invoke(inv, ChangedArgs);
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
            _stashedFrom = inv;
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
            _stashedFrom = null;
            if (_stashed.Count == 0) return;
            var list = Items(inv);
            for (int i = _stashed.Count - 1; i >= 0; i--) list.Add(_stashed[i]);
            _stashed.Clear();
        }

        /// <summary>How many items the last Stash lifted out - used by the self test.</summary>
        public static int StashedCount { get { return _stashed.Count; } }

        /// <summary>True while a lift is in progress. Nothing may encode the grid in this state.</summary>
        public static bool Lifted { get { return _stashDepth > 0; } }

        /// <summary>
        /// Force the lift back to "nothing in flight".
        ///
        /// <see cref="_stashDepth"/> is the single point of failure in the whole save design: it is
        /// a static counter, and if one Stash ever goes without its Unstash - an exception inside
        /// vanilla's Inventory.Save, a Harmony postfix skipped because the original threw, another
        /// mod's prefix returning false - then it never returns to zero. The NEXT save takes
        /// Stash's "already lifting" early return, lifts NOTHING, and vanilla happily writes the
        /// extra-slot items into its own package at out-of-grid positions. From there they are
        /// either deleted by a vanilla client or, because our own load grows the grid before
        /// vanilla places them, dropped straight back into the bag - which is exactly the "my
        /// extra slots emptied themselves into my inventory" report this exists to prevent.
        ///
        /// So every load and every spawn starts from a known state rather than trusting the
        /// counter. Anything still held is put back first, so a reset can never lose an item.
        /// </summary>
        public static void ResetLift()
        {
            if (_stashDepth == 0 && _stashed.Count == 0) return;

            // The inventory the lift was taken FROM, not Managed: a reset can happen before
            // Managed has been pointed at the new character (and the self test never sets it at
            // all), and putting the items back somewhere is the whole point of the reset.
            var inv = _stashedFrom ?? Managed;
            int held = _stashed.Count;
            if (inv != null && held > 0)
            {
                var list = Items(inv);
                for (int i = _stashed.Count - 1; i >= 0; i--)
                {
                    var item = _stashed[i];
                    if (item == null || list.Contains(item)) continue;
                    list.Add(item);
                }
            }

            NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] the save lift was still open (depth " +
                _stashDepth + ", holding " + held + " item(s)) - reset before it could make the next " +
                "save write those items into the vanilla package");

            _stashDepth = 0;
            _stashed.Clear();
            _stashedFrom = null;
        }

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

            // The blob is written from the LIVE grid, so it may only be written while the grid is
            // whole. If the lift is open the extra cells are empty by construction, and writing
            // here would replace a good blob with an empty one AND rotate the good one down into
            // bak1 - the character keeps its items (they are still in the list the lift holds) but
            // comes back next session with nothing in its slots. Refuse, loudly: an unchanged blob
            // is always recoverable, an overwritten one is not.
            if (Lifted)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Slots] REFUSED to write the save blob while " +
                    "the save lift was open (depth " + _stashDepth + ") - the extra cells are empty " +
                    "right now and writing would have replaced your saved items with nothing. The " +
                    "previous blob is untouched.");
                return;
            }

            // THE 0.8.7 GUARD, and the reason this bug happened at all.
            //
            // Collect() keys each item by the slot its cell belongs to and SKIPS any item whose
            // cell has no slot under it in the CURRENT layout. That is right for Collect - a
            // padding cell is Evacuate's problem, not the blob's - but it makes Collect LOSSY the
            // moment the layout and the grid disagree, which is exactly what a server pushing a
            // changed [Slots] count produces on a client that is already holding items. Writing
            // that result persisted "you own nothing" over a good blob and rotated the real one
            // down into bak1; the items themselves were still in the grid, so the next Orphans
            // pass moved them into the bag. One cause, both halves of the symptom.
            //
            // So: the blob must account for EVERY item in the extra area or it is not written.
            // A genuinely empty extra area is fine and still writes - that is a player emptying
            // their slots on purpose. What can never happen again is persisting a blob that is
            // short of what the grid is actually holding.
            var entries = Collect(inv);
            int live = ExtraItems(inv).Count;
            if (entries.Count < live)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Slots] REFUSED to write the save blob: the " +
                    "grid holds " + live + " extra-slot item(s) but only " + entries.Count +
                    " could be keyed to a slot in the current layout (" + SlotLayout.Describe() +
                    "). Writing would have saved " + (live - entries.Count) + " of them as gone. " +
                    "The previous blob is untouched; the unkeyed item(s) are: " + Unkeyed(inv));
                return;
            }

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

        /// <summary>
        /// Every extra-area item the current layout has no slot for, named with its cell - the
        /// list that explains a refused write, and the only way to tell afterwards WHICH cells the
        /// layout and the grid disagreed about.
        /// </summary>
        internal static string Unkeyed(Inventory inv)
        {
            var names = new List<string>();
            var extras = ExtraItems(inv);
            for (int i = 0; i < extras.Count; i++)
            {
                if (SlotLayout.At(extras[i].m_gridPos) != null) continue;
                names.Add(SlotBlob.Describe(extras[i]) + " at " + extras[i].m_gridPos.x + "," +
                          extras[i].m_gridPos.y);
            }
            return names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray());
        }

        /// <summary>The highest item count any backup holds, and which one. -1 when none decode.</summary>
        internal static int BestBackup(Dictionary<string, string> data, out int which)
        {
            which = 0;
            int best = -1;
            for (int n = 1; n <= Backups; n++)
            {
                var entries = ReadBlob(data, n);
                if (entries == null) continue;
                if (entries.Count > best) { best = entries.Count; which = n; }
            }
            return best;
        }

        /// <summary>
        /// How many of <paramref name="entries"/> the player does NOT already have somewhere in
        /// this inventory, matched on (prefab, quality, stack).
        ///
        /// This is the difference between a helpful offer and a duplication bug. When the extra
        /// slots were emptied by the off/on toggle, the items went into the BAG - they are still
        /// there, and re-injecting the backup on top of them would hand the player a second copy
        /// of everything. So an offer is only worth making, and a restore only worth doing, for
        /// items that are genuinely gone.
        /// </summary>
        internal static int MissingFrom(Inventory inv, List<SlotEntry> entries)
        {
            return NotAlreadyHeld(inv, entries).Count;
        }

        /// <summary>
        /// The subset of <paramref name="entries"/> the player is not already carrying. Each item
        /// in the inventory can satisfy only ONE entry, so two identical stacks in the blob need
        /// two in the bag to both count as present.
        /// </summary>
        internal static List<SlotEntry> NotAlreadyHeld(Inventory inv, List<SlotEntry> entries)
        {
            var outp = new List<SlotEntry>();
            if (entries == null) return outp;
            if (inv == null) { outp.AddRange(entries); return outp; }

            var have = new List<ItemDrop.ItemData>(Items(inv));
            for (int i = 0; i < entries.Count; i++)
            {
                var want = entries[i] == null ? null : entries[i].Item;
                if (want == null) continue;
                int at = -1;
                for (int j = 0; j < have.Count; j++)
                {
                    if (SlotBlob.PrefabNameOf(have[j]) != SlotBlob.PrefabNameOf(want)) continue;
                    if (have[j].m_quality != want.m_quality) continue;
                    if (have[j].m_stack != want.m_stack) continue;
                    at = j; break;
                }
                if (at >= 0) have.RemoveAt(at);   // consumed - it cannot match a second entry
                else outp.Add(entries[i]);
            }
            return outp;
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

                // A key written by an older layout (0.4.1's "quick1".."quick3") maps onto the slot
                // that replaced it before we fall back to "anywhere it fits", so a migrated item
                // keeps its place on the bottom row instead of shuffling.
                if (slot == null)
                {
                    var legacy = SlotLayout.LegacyKey(e.SlotKey);
                    if (legacy != null && inv.GetItemAt(legacy.Pos.x, legacy.Pos.y) == null &&
                        PlaceRaw(inv, e.Item, legacy.Pos))
                    {
                        placed++;
                        NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] migrated " + SlotBlob.Describe(e.Item) +
                            " from legacy slot '" + e.SlotKey + "' into '" + legacy.Key + "'");
                        continue;
                    }
                }

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

            // Every evacuation is a Warning, with the cell it came FROM and the reason. This is the
            // only trace left after the fact when a player reports that their extra slots emptied
            // themselves into their bag, and at Info level it was being lost in the noise.
            NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] EVACUATING " + items.Count +
                " item(s), reason: " + why);

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] evacuate (" + why + "): " +
                    SlotBlob.Describe(item) + " was at cell " + item.m_gridPos.x + "," +
                    item.m_gridPos.y + " (slot '" +
                    (SlotLayout.At(item.m_gridPos) == null ? "none" : SlotLayout.At(item.m_gridPos).Key) + "')");
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

                // NOWHERE to put it. The one thing we must never do here is nothing: the item was
                // taken out of the list at the top of this loop, so leaving it here would destroy
                // it outright (0.8.8 - the old message claimed it "stays in the save blob", which
                // is not true for an item that came out of a blob and was never in the list).
                // Putting it back is always recoverable; dropping it on the floor is not.
                if (list != null && !list.Contains(item)) list.Add(item);

                NoVikingLeftBehindPlugin.Log.LogError("[Slots] evacuate (" + why + "): NOWHERE to put " +
                    SlotBlob.Describe(item) + " (bag full and it could not be dropped) - it has been " +
                    "KEPT in your inventory rather than lost; free a slot and relog to tidy it up");
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
