using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Migration rescue for characters coming from shudnal's ExtraSlots (or any mod that parks
    /// items in the vanilla package at out-of-bounds grid positions).
    ///
    /// Inventory.Load (Inventory.cs:752) hands every saved item to the private
    ///   AddItem(string name, int stack, float durability, Vector2i pos, bool equipped, int quality,
    ///           int variant, long crafterID, string crafterName, Dictionary&lt;string,string&gt;
    ///           customData, int worldLevel, bool pickedUp)
    /// which forwards to AddItem(item, amount, x, y) whose FIRST statement is
    ///   if (x &lt; 0 || y &lt; 0 || x &gt;= m_width || y &gt;= m_height) return false;
    /// The caller ignores that false and destroys the temporary GameObject, so the item is gone
    /// for good. That is the exact deletion QOL-ARCHITECTURE section 4 opens with.
    ///
    /// We prefix the STRING overload rather than the inner one because it still has the prefab
    /// name in hand, so the rescued item can be rebuilt with SlotBlob.MakeItem - the same
    /// constructor the save blob uses. Capture is armed only around the local player's own
    /// Inventory.Load and is inert everywhere else, so containers behave exactly like vanilla.
    /// </summary>
    internal static class SlotsRescue
    {
        private static Inventory _captureInv;
        private static readonly List<ItemDrop.ItemData> _captured = new List<ItemDrop.ItemData>();
        private static readonly List<Vector2i> _capturedPos = new List<Vector2i>();

        public static bool Capturing { get { return _captureInv != null; } }
        public static int CapturedCount { get { return _captured.Count; } }

        public static void BeginCapture(Inventory inv)
        {
            _captureInv = inv;
            _captured.Clear();
            _capturedPos.Clear();
        }

        public static void EndCapture() { _captureInv = null; }

        /// <summary>Harmony prefix on the private Inventory.AddItem(string, ...) load path.</summary>
        public static bool AddItemLoadPrefix(Inventory __instance, string name, int stack, float durability,
                                             Vector2i pos, bool equipped, int quality, int variant,
                                             long crafterID, string crafterName,
                                             Dictionary<string, string> customData, int worldLevel,
                                             bool pickedUp, ref bool __result)
        {
            if (_captureInv == null || !ReferenceEquals(__instance, _captureInv)) return true;
            if (pos.x >= 0 && pos.y >= 0 && pos.x < __instance.GetWidth() &&
                pos.y < SlotStore.GetHeight(__instance)) return true;

            try
            {
                var item = SlotBlob.MakeItem(name, stack, durability, equipped, quality, variant,
                                             crafterID, crafterName, customData, worldLevel, pickedUp);
                if (item == null)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[Slots] RESCUE: '" + name + "' at grid " + pos.x +
                        "," + pos.y + " is out of bounds and its prefab is missing - cannot save it");
                    __result = false;
                    return false;
                }
                _captured.Add(item);
                _capturedPos.Add(pos);
                NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] RESCUE: captured " + SlotBlob.Describe(item) +
                    " from out-of-bounds grid position " + pos.x + "," + pos.y +
                    " (vanilla would have deleted it)");
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Slots] RESCUE failed for '" + name + "': " + e);
                return true;
            }
        }

        /// <summary>
        /// Put the captured items somewhere safe: a matching extra slot, then any free vanilla
        /// cell, then the player's feet. Returns a one-line summary, or null when nothing was
        /// captured. Safe to call with player == null (headless self test).
        /// </summary>
        public static string Place(Player player, Inventory inv)
        {
            if (_captured.Count == 0) return null;
            int toSlots = 0;
            var leftovers = new List<ItemDrop.ItemData>();

            for (int i = 0; i < _captured.Count; i++)
            {
                var item = _captured[i];
                var slot = SlotLayout.FindFreeFor(inv, item);
                if (slot != null && SlotStore.PlaceRaw(inv, item, slot.Pos))
                {
                    toSlots++;
                    NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] RESCUE: " + SlotBlob.Describe(item) +
                        " -> extra slot '" + slot.Key + "'");
                    continue;
                }
                leftovers.Add(item);
            }

            int toBag = 0;
            for (int i = leftovers.Count - 1; i >= 0; i--)
            {
                var free = SlotStore.FindFreeVanillaCell(inv);
                if (free.x < 0) break;
                if (!SlotStore.PlaceRaw(inv, leftovers[i], free)) break;
                NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] RESCUE: " + SlotBlob.Describe(leftovers[i]) +
                    " -> inventory cell " + free.x + "," + free.y);
                leftovers.RemoveAt(i);
                toBag++;
            }

            int dropped = leftovers.Count;
            if (dropped > 0) SlotStore.Evacuate(player, inv, leftovers, "migration rescue");

            SlotStore.Changed(inv);
            var summary = "[Slots] RESCUE: recovered " + _captured.Count + " item(s) that vanilla would have " +
                          "deleted - " + toSlots + " into extra slots, " + toBag + " into the bag, " +
                          dropped + " evacuated";
            NoVikingLeftBehindPlugin.Log.LogWarning(summary);
            if (player != null)
                player.Message(MessageHud.MessageType.Center,
                    "NVLB recovered " + _captured.Count + " item(s) from your old extra slots");

            _captured.Clear();
            _capturedPos.Clear();
            return summary;
        }
    }
}
