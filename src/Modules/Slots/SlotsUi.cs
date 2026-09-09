using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Everything that draws. Deliberately the only file in the module that touches Unity UI, and
    /// deliberately installed with its own try/catch: if a game update moves the inventory layout
    /// about, Install() logs and gives up, the rest of ExtraSlots carries on, and the ITEMS - which
    /// live in the save blob, not in the UI - are untouched. Turn [Slots] ShowUI off for the same
    /// effect without a code change.
    ///
    /// HOW THE PANEL WORKS (adapted from shudnal's ExtraSlots, EquipmentPanel.cs, public domain)
    ///
    /// There is still no second InventoryGrid, and that is the whole trick. InventoryGrid.UpdateGui
    /// (InventoryGrid.decompiled.cs:195) instantiates one cell per inventory position, row-major
    /// into m_gridRoot, and every cell carries the vanilla machinery already: the Button and
    /// UIInputHandler that raise left/right click, the UITooltip, the durability GuiBar, the stack
    /// and quality labels, the equipped tick. So instead of building a UI we simply MOVE the cells
    /// for rows 4+ out of the grid flow and into a panel of our own:
    ///
    ///   1. m_gridRoot is squeezed back to four rows' worth of height, so the main grid measures
    ///      and looks exactly like vanilla again (UpdateGui had grown it to the full height).
    ///   2. Each extra cell's RectTransform.anchoredPosition is set to its SlotDef.PanelTile
    ///      offset from the panel's top-left corner. The cells stay children of m_gridRoot, so
    ///      hover, drag, drop, tooltips and clicks keep working with no code from us - the game
    ///      hit-tests each cell's own rect (InventoryGrid.GetHoveredElement) rather than the grid.
    ///   3. A background is cloned from the inventory window's own "Bkg" and "Darken" children, so
    ///      the panel is the same wood as the window, in the same theme, at the same UI scale, and
    ///      any UI mod that reskins the inventory reskins the panel too.
    ///
    /// Because the panel is a child of InventoryGui.m_player it inherits the window's show/hide,
    /// its canvas scaling and its animation. The container window opens ABOVE the player window in
    /// Valheim, not beside it, so a right-hand panel never collides with it and needs no special
    /// handling; it hides with the inventory like everything else.
    ///
    /// Cells for extra positions with no slot behind them (layout padding) are switched off, so a
    /// half-used row never shows as dead squares.
    /// </summary>
    internal static class SlotsUi
    {
        private const string PanelName = "NVLB extra slots";

        /// <summary>shudnal's tile geometry: a 64 px slot plus 6 px of air.</summary>
        private const float TileSpace = 6f;
        private const float TileSize = 64f + TileSpace;

        /// <summary>Gap between the right edge of the inventory window and the panel.</summary>
        private const float WindowGap = 100f;

        private static Func<bool> _enabled;
        private static Func<Vector2> _offset;
        private static Func<float> _scale;
        private static Func<int, string> _quickLabel;
        private static bool _installed;
        private static int _errors;

        // Scene objects. All dropped in Forget() when InventoryGui is destroyed, so a world
        // change or a hot reload rebuilds them from the new window instead of holding a corpse.
        private static RectTransform _panel;
        private static Image _panelImage;
        private static RectTransform _selectedFrame;
        private static RectTransform _invBkg;
        private static Image _invBkgImage;

        private static Sprite _ammoIcon;
        private static bool _ammoIconTried;
        private static Material _iconMaterial;
        private static Vector3 _iconScale = Vector3.zero;

        private static Color _normal = Color.clear;
        private static Color _highlighted = Color.clear;
        private static Color _normalUnfit = Color.clear;
        private static Color _highlightedUnfit = Color.clear;

        /// <summary>True while the extra cells are parked back in their vanilla grid positions.</summary>
        private static bool _vanillaPositions = true;

        public static void Install(Harmony harmony, Func<bool> enabled, Func<Vector2> offset,
                                   Func<float> scale, Func<int, string> quickLabel)
        {
            _enabled = enabled;
            _offset = offset;
            _scale = scale;
            _quickLabel = quickLabel;
            try
            {
                var updateGui = AccessTools.Method(typeof(InventoryGrid), "UpdateGui",
                    new[] { typeof(Player), typeof(ItemDrop.ItemData) });
                var guiDestroy = AccessTools.Method(typeof(InventoryGui), "OnDestroy");
                if (updateGui == null || guiDestroy == null)
                    throw new Exception("InventoryGrid.UpdateGui / InventoryGui.OnDestroy not found");

                harmony.Patch(updateGui, postfix: new HarmonyMethod(typeof(SlotsUi), nameof(DrawPanel)));
                harmony.Patch(guiDestroy, postfix: new HarmonyMethod(typeof(SlotsUi), nameof(Forget)));
                _installed = true;
                NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] side-panel UI patches installed");
            }
            catch (Exception e)
            {
                _installed = false;
                NoVikingLeftBehindPlugin.Log.LogError("[Slots] UI could not be installed - the extra slots " +
                    "still work and your items are safe, they are just not drawn: " + e.Message);
            }
        }

        public static bool Installed { get { return _installed; } }

        /// <summary>Force the panel to be rebuilt (layout or config changed).</summary>
        public static void Invalidate()
        {
            if (_panel != null)
            {
                try { UnityEngine.Object.Destroy(_panel.gameObject); } catch { }
                _panel = null;
                _panelImage = null;
            }
            if (_selectedFrame != null)
            {
                try { UnityEngine.Object.Destroy(_selectedFrame.gameObject); } catch { }
                _selectedFrame = null;
            }
        }

        /// <summary>InventoryGui went away (world change, hot reload). Everything cached is stale.</summary>
        public static void Forget()
        {
            _panel = null;
            _panelImage = null;
            _selectedFrame = null;
            _invBkg = null;
            _invBkgImage = null;
            _iconMaterial = null;
            _iconScale = Vector3.zero;
            _normal = _highlighted = _normalUnfit = _highlightedUnfit = Color.clear;
            _vanillaPositions = true;
        }

        private static bool On()
        {
            return _installed && _enabled != null && _enabled() && SlotStore.Managed != null;
        }

        // ---- the draw --------------------------------------------------------------------------

        /// <summary>
        /// Runs after every InventoryGrid.UpdateGui, which InventoryGui.Update calls once a frame
        /// while the inventory is open. Idempotent by construction: it sets absolute positions and
        /// absolute colours, never deltas.
        /// </summary>
        private static void DrawPanel(InventoryGrid __instance)
        {
            if (!_installed || __instance == null) return;
            try
            {
                var gui = InventoryGui.instance;
                if (gui == null || !ReferenceEquals(__instance, gui.m_playerGrid)) return;

                var elements = __instance.m_elements;
                if (elements == null || elements.Count == 0) return;

                // Cache the vanilla icon material and scale from a cell we never touch, so that a
                // slot which later receives an item can be handed them back.
                if (_iconMaterial == null && elements[0].m_icon != null && elements[0].m_icon.material != null)
                {
                    _iconMaterial = elements[0].m_icon.material;
                    _iconScale = elements[0].m_icon.transform.localScale;
                }

                if (!On() || !ReferenceEquals(__instance.GetInventory(), SlotStore.Managed))
                {
                    RestoreVanilla(__instance);
                    return;
                }

                float space = __instance.m_elementSpace > 1f ? __instance.m_elementSpace : TileSize;

                // 1. The main grid is four rows tall again, whatever the inventory's real height is.
                if (__instance.m_gridRoot != null)
                    __instance.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                                                                    SlotLayout.VanillaHeight * space);

                float scale = Mathf.Clamp(_scale != null ? _scale() : 1f, 0.4f, 2.5f);
                float tile = TileSize * scale;
                Vector2 origin = PanelOrigin(gui);
                Vector2 size = new Vector2(SlotLayout.PanelTilesWide * tile + TileSpace * scale * 0.5f,
                                           SlotLayout.PanelTilesHigh * tile + TileSpace * scale * 0.5f);

                EnsurePanel(gui, origin, size);

                var drag = gui.m_dragItem;
                int first = SlotLayout.VanillaWidth * SlotLayout.VanillaHeight;

                // 2. Every extra cell goes where the panel wants it.
                for (int i = first; i < elements.Count; i++)
                {
                    var el = elements[i];
                    if (el == null || el.gameObject == null) continue;

                    int x = i % SlotLayout.VanillaWidth;
                    int y = i / SlotLayout.VanillaWidth;
                    var slot = SlotLayout.At(x, y);
                    if (slot == null) { el.gameObject.SetActive(false); continue; }   // padding cell

                    el.gameObject.SetActive(true);
                    var rt = el.transform as RectTransform;
                    if (rt != null)
                    {
                        rt.localScale = Vector3.one * scale;
                        rt.anchoredPosition = origin + new Vector2(slot.PanelTile.x * tile * 0.25f,
                                                                   -slot.PanelTile.y * tile * 0.25f);
                    }

                    Label(el, slot);
                    Decorate(__instance, el, slot);
                    Tint(el, drag != null && !SlotLayout.Accepts(slot, drag));
                }

                _vanillaPositions = false;
            }
            catch (Exception e) { Complain("panel draw", e); }
        }

        /// <summary>
        /// The panel's top-left corner, in m_gridRoot's local space. shudnal's formula: the grid
        /// root's origin sits at the left edge of the inventory window, so "window width + a gap"
        /// lands just outside the window's right edge, level with the top row of the grid.
        /// </summary>
        private static Vector2 PanelOrigin(InventoryGui gui)
        {
            float windowWidth = gui.m_player != null ? gui.m_player.rect.width : 0f;
            Vector2 off = _offset != null ? _offset() : Vector2.zero;
            return new Vector2(windowWidth + WindowGap + off.x, -off.y);
        }

        // ---- the background --------------------------------------------------------------------

        /// <summary>
        /// Build (once) and size (every frame) the wooden panel behind the slots. Cloned from the
        /// inventory window's own "Bkg" and "Darken", and its sprite/colour re-copied from them
        /// each frame so a reskin or a theme change carries across - straight from shudnal's
        /// EquipmentPanel.UpdateEquipmentBackground.
        /// </summary>
        private static void EnsurePanel(InventoryGui gui, Vector2 origin, Vector2 size)
        {
            if (gui.m_player == null) return;

            if (_invBkg == null)
            {
                var t = gui.m_player.Find("Bkg");
                if (t != null) _invBkg = t as RectTransform;
                if (_invBkg != null) _invBkgImage = _invBkg.GetComponent<Image>();
            }

            if (_panel == null)
            {
                if (_invBkg == null) return;   // unknown window layout: slots still draw, bare

                var darken = gui.m_player.Find("Darken") as RectTransform;
                var groupHandler = gui.m_player.GetComponent<UIGroupHandler>();
                Transform frames = (groupHandler != null && groupHandler.m_enableWhenActiveAndGamepad != null)
                    ? groupHandler.m_enableWhenActiveAndGamepad.transform : null;

                _panel = new GameObject(PanelName, typeof(RectTransform)).GetComponent<RectTransform>();
                _panel.gameObject.layer = _invBkg.gameObject.layer;
                _panel.SetParent(gui.m_player, worldPositionStays: false);
                // Behind the slots (which live in m_gridRoot, later in the hierarchy) but in front
                // of the window's own darkening layer and gamepad frames.
                int behind = frames != null ? frames.GetSiblingIndex()
                           : (darken != null ? darken.GetSiblingIndex() : 0);
                _panel.SetSiblingIndex(behind + 1);
                _panel.anchorMin = new Vector2(0f, 1f);
                _panel.anchorMax = new Vector2(0f, 1f);
                _panel.offsetMin = Vector2.zero;
                _panel.offsetMax = Vector2.zero;

                if (darken != null)
                {
                    var d = UnityEngine.Object.Instantiate(darken, _panel);
                    d.name = "Darken";
                    d.sizeDelta = Vector2.one * 70f;   // the window's 100 is far too much for a small panel
                }

                var bkg = UnityEngine.Object.Instantiate(_invBkg.transform, _panel);
                bkg.name = "Bkg";
                _panelImage = bkg.GetComponent<Image>();

                if (frames != null && frames.childCount > 0 && frames.GetChild(0) is RectTransform sample)
                {
                    _selectedFrame = UnityEngine.Object.Instantiate(sample, frames);
                    _selectedFrame.name = "selected (NVLB slots)";
                    _selectedFrame.anchorMin = _panel.anchorMin;
                    _selectedFrame.anchorMax = _panel.anchorMax;
                    _selectedFrame.offsetMin = Vector2.zero;
                    _selectedFrame.offsetMax = Vector2.zero;
                }

                NoVikingLeftBehindPlugin.Log.LogInfo("[Slots] side panel built (" +
                    SlotLayout.PanelTilesWide.ToString("0.##") + " x " +
                    SlotLayout.PanelTilesHigh.ToString("0.##") + " tiles)");
            }

            _panel.sizeDelta = size;
            _panel.anchoredPosition = origin + new Vector2(size.x * 0.5f, -size.y * 0.5f);

            if (_panelImage != null && _invBkgImage != null)
            {
                _panelImage.sprite = _invBkgImage.sprite;
                _panelImage.overrideSprite = _invBkgImage.overrideSprite;
                _panelImage.color = _invBkgImage.color;
            }

            if (_selectedFrame != null)
            {
                _selectedFrame.sizeDelta = size + Vector2.one * 26f;
                _selectedFrame.anchoredPosition = _panel.anchoredPosition;
            }
        }

        // ---- per-slot decoration ---------------------------------------------------------------

        /// <summary>
        /// Short horizontal caption, drawn in the cell's own "binding" text - the same TMP_Text
        /// vanilla uses for the 1-8 hotbar numbers on the top row. Sized to the whole cell and left
        /// to TMP's auto-sizing, so it never runs vertically the way a fixed narrow rect makes it.
        /// </summary>
        private static void Label(InventoryElement el, SlotDef slot)
        {
            var binding = el.transform.Find("binding");
            if (binding == null) return;
            var text = binding.GetComponent<TMP_Text>();
            var rt = binding as RectTransform;
            if (text == null || rt == null) return;

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;

            string caption = slot.Kind == SlotKind.Quick
                ? (_quickLabel != null ? _quickLabel(slot.Index - 1) : "")
                : slot.PanelLabel;
            if (caption == null) caption = "";

            text.text = caption;
            text.enabled = caption.Length > 0;
            text.enableAutoSizing = true;
            text.fontSizeMin = 9f;
            text.fontSizeMax = 15f;
            text.overflowMode = TextOverflowModes.Overflow;
            text.horizontalAlignment = HorizontalAlignmentOptions.Center;
            text.verticalAlignment = VerticalAlignmentOptions.Top;
            text.margin = new Vector4(0f, 2f, 0f, 0f);
            text.color = slot.Kind == SlotKind.Quick
                ? new Color(1f, 0.86f, 0.45f, 0.95f)      // hotkey: the game's own highlight yellow
                : new Color(0.88f, 0.84f, 0.72f, 0.85f);  // slot name: parchment
        }

        /// <summary>
        /// Hint icon and tooltip for an EMPTY slot: the cell's own fork sprite for food (the same
        /// Image vanilla lights up on a food item), an arrow for ammo. A slot holding an item is
        /// left entirely to vanilla, except that the icon material and scale we borrowed for the
        /// hint have to be handed back.
        /// </summary>
        private static void Decorate(InventoryGrid grid, InventoryElement el, SlotDef slot)
        {
            if (el.m_used)
            {
                if (el.m_icon != null)
                {
                    if (el.m_icon.material == null && _iconMaterial != null) el.m_icon.material = _iconMaterial;
                    if (_iconScale != Vector3.zero) el.m_icon.transform.localScale = _iconScale;
                }
                return;
            }

            if (slot.Kind == SlotKind.Food && el.m_food != null)
            {
                el.m_food.enabled = true;
                el.m_food.color = Color.grey - new Color(0f, 0f, 0f, 0.5f);
            }
            else if (slot.Kind == SlotKind.Ammo && el.m_icon != null)
            {
                var sprite = AmmoIcon();
                if (sprite != null)
                {
                    el.m_icon.enabled = true;
                    el.m_icon.material = null;
                    el.m_icon.sprite = sprite;
                    el.m_icon.transform.localScale = Vector3.one * 0.8f;
                    el.m_icon.color = Color.grey - new Color(0f, 0f, 0f, 0.35f);
                }
            }

            if (el.m_tooltip != null)
                el.m_tooltip.Set(slot.Label + " slot", TooltipFor(slot), grid.m_tooltipAnchor);
        }

        private static string TooltipFor(SlotDef slot)
        {
            switch (slot.Kind)
            {
                case SlotKind.Food: return "Only food fits here. It is eaten automatically when the buff runs out.";
                case SlotKind.Ammo: return "Only arrows and bolts fit here.";
                case SlotKind.Quick: return "Anything fits here. Press its key to use it.";
                case SlotKind.Generic: return "Anything fits here. Plain storage - no hotkey.";
                case SlotKind.Utility: return "Only utility items fit here - a belt, the Wishbone.";
                default: return "Only " + slot.Label.ToLowerInvariant() + " armour fits here. It is worn while it sits in the slot.";
            }
        }

        /// <summary>The game's own wooden-arrow icon, borrowed as the empty ammo-slot hint.</summary>
        private static Sprite AmmoIcon()
        {
            if (_ammoIconTried) return _ammoIcon;
            _ammoIconTried = true;
            try
            {
                if (ObjectDB.instance == null) { _ammoIconTried = false; return null; }
                var prefab = ObjectDB.instance.GetItemPrefab("ArrowWood");
                if (prefab == null) return null;
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) return null;
                _ammoIcon = drop.m_itemData.GetIcon();
            }
            catch { _ammoIcon = null; }
            return _ammoIcon;
        }

        /// <summary>
        /// Redden a slot the item being dragged cannot go into, so the per-slot filters are visible
        /// before the drop is refused. shudnal's SetSlotColor, with his colour deltas.
        /// </summary>
        private static void Tint(InventoryElement el, bool unfit)
        {
            var button = el.GetComponent<Button>();
            if (button == null) return;

            if (_normal == Color.clear)
            {
                _normal = button.colors.normalColor;
                _highlighted = button.colors.highlightedColor;
                _normalUnfit = _normal + new Color(0.3f, 0f, 0f, 0.1f);
                _highlightedUnfit = _highlighted + new Color(0.3f, 0f, 0f, 0.1f);
            }

            var colors = button.colors;
            colors.normalColor = unfit ? _normalUnfit : _normal;
            colors.highlightedColor = unfit ? _highlightedUnfit : _highlighted;
            button.colors = colors;
        }

        // ---- the kill switch -------------------------------------------------------------------

        /// <summary>
        /// [Slots] ShowUI off, or a grid that is not the player's. Put every cell back exactly
        /// where InventoryGrid.UpdateGui would have put it and let go of the panel: the extra rows
        /// then draw as plain extra rows under the bag - ugly, but every item stays reachable with
        /// the mouse, which is what a kill switch is for.
        /// </summary>
        private static void RestoreVanilla(InventoryGrid grid)
        {
            if (_vanillaPositions && _panel == null) return;
            if (!ReferenceEquals(grid, InventoryGui.instance != null ? InventoryGui.instance.m_playerGrid : null)) return;

            Invalidate();

            var elements = grid.m_elements;
            var rect = grid.transform as RectTransform;
            if (elements == null || rect == null) { _vanillaPositions = true; return; }

            int w = grid.m_width;
            int h = grid.m_height;
            if (w <= 0) { _vanillaPositions = true; return; }

            float space = grid.m_elementSpace;
            Vector2 widget = new Vector2(w * space, h * space);
            Vector2 baseAt = new Vector2(rect.rect.width * 0.5f, 0f) - new Vector2(widget.x, 0f) * 0.5f;

            for (int i = 0; i < elements.Count; i++)
            {
                var el = elements[i];
                if (el == null || el.gameObject == null) continue;
                el.gameObject.SetActive(true);
                var rt = el.transform as RectTransform;
                if (rt == null) continue;
                rt.localScale = Vector3.one;
                rt.anchoredPosition = baseAt + new Vector2((i % w) * space, -(i / w) * space);
            }

            if (grid.m_gridRoot != null)
                grid.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h * space);

            _vanillaPositions = true;
        }

        private static void Complain(string what, Exception e)
        {
            if (_errors++ >= 3) return;
            NoVikingLeftBehindPlugin.Log.LogError("[Slots] UI " + what + " failed (" + _errors + "/3): " + e.Message);
        }
    }
}
