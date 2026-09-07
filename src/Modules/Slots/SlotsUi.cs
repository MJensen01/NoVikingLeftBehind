using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Everything that draws. Deliberately the only file in the module that touches Unity UI, and
    /// deliberately installed with its own try/catch: if a game update moves the inventory layout
    /// about, Install() logs and gives up, the rest of ExtraSlots carries on, and the ITEMS - which
    /// live in the save blob, not in the UI - are untouched. Turn [Slots] ShowUI off for the same
    /// effect without a code change.
    ///
    /// There is no custom panel. InventoryGrid.UpdateGui (InventoryGrid.decompiled.cs:195) rebuilds
    /// its whole cell grid from m_inventory.GetWidth()/GetHeight() and instantiates the cells into
    /// m_gridRoot row-major, so growing the player's inventory height is enough to make the extra
    /// slots appear with working drag and drop, tooltips, durability bars and equip highlights.
    /// All this file does is label them and make the panel tall enough to show them.
    /// </summary>
    internal static class SlotsUi
    {
        private static Func<bool> _enabled;
        private static bool _installed;
        private static int _appliedRows = -1;
        private static Vector2 _baseSize;
        private static bool _haveBaseSize;
        private static int _errors;

        public static void Install(Harmony harmony, Func<bool> enabled)
        {
            _enabled = enabled;
            try
            {
                var updateGui = AccessTools.Method(typeof(InventoryGrid), "UpdateGui",
                    new[] { typeof(Player), typeof(ItemDrop.ItemData) });
                var updateInv = AccessTools.Method(typeof(InventoryGui), "UpdateInventory",
                    new[] { typeof(Player) });
                if (updateGui == null || updateInv == null)
                    throw new Exception("InventoryGrid.UpdateGui / InventoryGui.UpdateInventory not found");

                harmony.Patch(updateGui, postfix: new HarmonyMethod(typeof(SlotsUi), nameof(LabelExtraCells)));
                harmony.Patch(updateInv, postfix: new HarmonyMethod(typeof(SlotsUi), nameof(ResizePanel)));
                _installed = true;
                NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] UI patches installed");
            }
            catch (Exception e)
            {
                _installed = false;
                NoVikingLeftBehindPlugin.Log.LogError("[Slots] UI could not be installed - the extra slots " +
                    "still work and your items are safe, they are just not drawn: " + e.Message);
            }
        }

        public static bool Installed { get { return _installed; } }

        /// <summary>Force the panel size to be recomputed (layout or config changed).</summary>
        public static void Invalidate() { _appliedRows = -1; }

        private static bool On()
        {
            return _installed && _enabled != null && _enabled() && SlotStore.Managed != null;
        }

        /// <summary>Write the slot name into each extra cell's "binding" label.</summary>
        private static void LabelExtraCells(InventoryGrid __instance)
        {
            if (!On()) return;
            try
            {
                if (!ReferenceEquals(__instance.GetInventory(), SlotStore.Managed)) return;
                var root = __instance.m_gridRoot;
                if (root == null) return;

                int w = SlotLayout.VanillaWidth;
                int total = w * SlotLayout.TotalHeight;
                if (root.childCount < total) return;   // grid not rebuilt for the new height yet

                for (int y = SlotLayout.VanillaHeight; y < SlotLayout.TotalHeight; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var cell = root.GetChild(y * w + x);
                        if (cell == null) continue;
                        var binding = cell.Find("binding");
                        if (binding == null) continue;
                        var text = binding.GetComponent<TMP_Text>();
                        if (text == null) continue;

                        var slot = SlotLayout.At(x, y);
                        text.enabled = true;
                        text.text = slot != null ? slot.Label : "-";
                        text.color = slot != null ? new Color(0.85f, 0.8f, 0.6f, 0.9f)
                                                  : new Color(0.5f, 0.5f, 0.5f, 0.45f);
                    }
                }
            }
            catch (Exception e) { Complain("labelling", e); }
        }

        /// <summary>Make the player panel tall enough for the extra rows.</summary>
        private static void ResizePanel(InventoryGui __instance)
        {
            if (!On()) return;
            try
            {
                if (_appliedRows == SlotLayout.Rows) return;
                var panel = __instance.m_player;
                var grid = __instance.m_playerGrid;
                if (panel == null || grid == null) return;

                if (!_haveBaseSize) { _baseSize = panel.sizeDelta; _haveBaseSize = true; }
                panel.sizeDelta = new Vector2(_baseSize.x, _baseSize.y + SlotLayout.Rows * grid.m_elementSpace);
                _appliedRows = SlotLayout.Rows;
                NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] inventory panel grown by " + SlotLayout.Rows +
                                                     " row(s) (" + grid.m_elementSpace + " each)");
            }
            catch (Exception e) { Complain("panel resize", e); }
        }

        private static void Complain(string what, Exception e)
        {
            if (_errors++ >= 3) return;
            NoVikingLeftBehindPlugin.Log.LogError("[Slots] UI " + what + " failed (" + _errors + "/3): " + e.Message);
        }
    }
}
