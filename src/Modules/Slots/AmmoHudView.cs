using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The ammo readout: one hotbar-style tile per non-empty ammo slot, bottom-left of the screen.
    ///
    /// The mod ships no UI assets, so the tile is not drawn - it is <c>Instantiate</c>d from
    /// vanilla's own hotbar element, <c>HotkeyBar.m_elementPrefab</c> (HotkeyBar.cs:26). That one
    /// prefab carries the whole vanilla look: the slot background sprite, the icon
    /// <see cref="Image"/>, the amount <see cref="TMP_Text"/> in Valheim's own font, the durability
    /// bar and the "equiped" marker. Vanilla finds those leaves by name -
    /// <c>Find("icon") / Find("durability") / Find("amount") / Find("equiped") / Find("queued") /
    /// Find("selected") / Find("binding")</c> (HotkeyBar.cs:110-118) - and so do we, so a tile here
    /// is the same widget the hotbar draws, at the same size, in the same font, at the same opacity.
    ///
    /// What is deliberately switched off on our copies:
    ///   * <c>binding</c> - the little "1".."8" hotbar number. These slots have no hotbar key.
    ///   * <c>queued</c> / <c>selected</c> - equip-queue and gamepad-selection state, neither of
    ///     which means anything for a passive readout.
    ///   * <c>durability</c> - arrows and bolts have no durability.
    ///   * the prefab's <c>Button</c> (HotkeyBar.cs:104) and every raycast target, so the readout
    ///     can never swallow a click meant for the world behind it.
    ///
    /// The equipped stack is marked with vanilla's OWN marker - the <c>equiped</c> child, which is
    /// exactly what <c>HotkeyBar.UpdateIcons</c> lights up for the equipped hotbar item
    /// (<c>elementData2.m_equiped.SetActive(itemData.m_equipped)</c>, HotkeyBar.cs:150) - plus a
    /// small alpha lift, so the quiver you are actually shooting reads first at a glance.
    ///
    /// Work per frame is deliberately tiny. The inventory is only re-read when something says it
    /// changed (<see cref="Invalidate"/>, driven by <c>Player.OnInventoryChanged</c>); every other
    /// frame compares a handful of ints and booleans against what was last written and touches
    /// Unity only where they differ. The single string this class can ever build is the amount
    /// label, on the one frame a stack size changes - the same cache vanilla itself keeps
    /// (<c>ElementData.m_stackText</c>, HotkeyBar.cs:28 and :154-158).
    ///
    /// Every build step runs in its own try/catch and logs a <c>[AmmoHud]</c> line naming the step;
    /// any failure logs once, tears the widget down and permanently skips it. The extra slots
    /// themselves keep working - you just do not get the readout.
    /// </summary>
    internal static class AmmoHudView
    {
        /// <summary>Player nudge on top of the derived anchor, in pixels. [AmmoHud] OffsetX/OffsetY.</summary>
        public static Vector2 Offset = Vector2.zero;

        /// <summary>Size relative to vanilla's hotbar tile. 1 = exactly the hotbar's size.</summary>
        public static float Scale = 1f;

        /// <summary>Opacity of the whole readout. [AmmoHud] Alpha.</summary>
        public static float Alpha = 0.9f;

        /// <summary>Opacity of a tile that is NOT the equipped ammo, relative to the tile itself.</summary>
        private const float DimAlpha = 0.78f;

        /// <summary>Pixels kept between our row and whatever vanilla widget it sits above.</summary>
        private const float Gap = 14f;

        /// <summary>Where the row goes when no vanilla widget could be measured. Bottom-left, clear of everything.</summary>
        private static readonly Vector2 FallbackAnchor = new Vector2(64f, 96f);

        private sealed class Tile
        {
            public GameObject Go;
            public CanvasGroup Group;
            public Image Icon;
            public TMP_Text Amount;
            public GameObject Equipped;

            /// <summary>The item this tile is currently showing. Re-read only on a dirty frame.</summary>
            public ItemDrop.ItemData Item;

            // Last values actually written, so a quiet frame writes nothing and allocates nothing.
            public Sprite ShownSprite;
            public int ShownStack = int.MinValue;
            public bool ShownEquipped;
            public bool ShownAmountOn;
            public float ShownAlpha = -1f;
            public bool ShownActive = true;
        }

        private static Hud _hud;
        private static GameObject _root;
        private static RectTransform _rootRt;
        private static CanvasGroup _rootGroup;
        private static Tile[] _tiles = new Tile[0];
        private static Vector2 _anchor = FallbackAnchor;
        private static float _spacing = 70f;

        private static bool _built;
        private static bool _failed;
        private static bool _visible;
        private static bool _dirty = true;
        private static int _filled;
        private static int _builtFor = -1;

        private static void Info(string s) { NoVikingLeftBehindPlugin.Log.LogInfo("[AmmoHud] " + s); }
        private static void Warn(string s) { NoVikingLeftBehindPlugin.Log.LogWarning("[AmmoHud] " + s); }

        // ---- public API ---------------------------------------------------------------------

        /// <summary>The inventory changed (or the layout did): re-read every tile on the next frame.</summary>
        public static void Invalidate() { _dirty = true; }

        /// <summary>Push the live config values. Cheap and idempotent; safe before the build.</summary>
        public static void Configure(Vector2 offset, float scale, float alpha)
        {
            Offset = offset;
            Scale = Mathf.Clamp(scale, 0.2f, 4f);
            Alpha = Mathf.Clamp01(alpha);
            try { ApplyLayout(); }
            catch (Exception e) { Warn("could not apply the new offset/scale/alpha: " + e.Message); }
        }

        /// <summary>
        /// The ammo-slot items in slot order. <paramref name="into"/> is filled with one entry per
        /// ammo slot (null for an empty one) and the number of NON-empty slots is returned. Pure:
        /// no UI, no Player, no Hud - which is what lets the headless self test exercise it.
        /// </summary>
        internal static int Collect(Inventory inv, ItemDrop.ItemData[] into)
        {
            if (into == null) return 0;
            int filled = 0;
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = null;
                if (inv == null || i >= SlotLayout.AmmoCount) continue;
                var slot = SlotLayout.ByKey("ammo" + (i + 1));
                if (slot == null) continue;
                var item = inv.GetItemAt(slot.Pos.x, slot.Pos.y);
                if (item == null || item.m_shared == null) continue;
                into[i] = item;
                filled++;
            }
            return filled;
        }

        /// <summary>
        /// One frame. Called from the module's <c>Hud.Update</c> postfix, already gated on the
        /// module being live.
        /// </summary>
        public static void Tick(Hud hud, Player player)
        {
            if (_failed) return;
            try
            {
                if (hud == null || !ShouldShow(hud, player)) { SetVisible(false); return; }
                if (!Ensure(hud)) return;

                if (_dirty)
                {
                    _dirty = false;
                    Reread(player.GetInventory());
                }
                RefreshLive(player);
                SetVisible(_filled > 0);
            }
            catch (Exception e)
            {
                Fail("update failed: " + e.Message);
            }
        }

        // ---- visibility ----------------------------------------------------------------------

        /// <summary>
        /// The same gate vanilla's own hotbar uses to decide the player is "in the game and looking
        /// at the world" (HotkeyBar.cs:40), plus the two HUD-level ones: <c>Hud.IsUserHidden()</c>
        /// (Hud.cs:1752 - Ctrl+F3 or the gamepad toggle) and <c>Hud.IsVisible()</c> (Hud.cs:462),
        /// which vanilla itself turns off for a cutscene (<c>SetVisible(!m_userHidden &amp;&amp;
        /// !localPlayer.InCutscene())</c>, Hud.cs:524).
        /// </summary>
        private static bool ShouldShow(Hud hud, Player player)
        {
            if (player == null || player.IsDead()) return false;
            if (Hud.IsUserHidden() || !hud.IsVisible()) return false;
            if (InventoryGui.IsVisible() || StoreGui.IsVisible() || Menu.IsVisible()) return false;
            if (Minimap.IsOpen()) return false;
            if (Hud.IsPieceSelectionVisible() || Hud.InRadial()) return false;
            return true;
        }

        private static void SetVisible(bool on)
        {
            if (_visible == on) return;
            _visible = on;
            if (_root != null) _root.SetActive(on);
        }

        // ---- refresh ------------------------------------------------------------------------------

        /// <summary>
        /// The inventory changed: re-read which item sits in each ammo slot and repoint the icons.
        /// This is the only path that walks the inventory or touches a sprite.
        /// </summary>
        private static void Reread(Inventory inv)
        {
            _filled = 0;
            for (int i = 0; i < _tiles.Length; i++)
            {
                var tile = _tiles[i];
                if (tile == null) continue;

                ItemDrop.ItemData item = null;
                if (inv != null && i < SlotLayout.AmmoCount)
                {
                    var slot = SlotLayout.ByKey("ammo" + (i + 1));
                    if (slot != null) item = inv.GetItemAt(slot.Pos.x, slot.Pos.y);
                }
                if (item != null && item.m_shared == null) item = null;

                tile.Item = item;
                bool on = item != null;
                if (on) _filled++;

                if (tile.ShownActive != on)
                {
                    tile.ShownActive = on;
                    if (tile.Go != null) tile.Go.SetActive(on);
                }
                if (!on) continue;

                if (tile.Icon != null)
                {
                    var sprite = item.GetIcon();
                    if (!ReferenceEquals(tile.ShownSprite, sprite))
                    {
                        tile.ShownSprite = sprite;
                        tile.Icon.sprite = sprite;
                    }
                }

                // Vanilla's own "only stackables show an amount" rule (HotkeyBar.cs:152-161).
                if (tile.Amount != null)
                {
                    bool stackable = item.m_shared.m_maxStackSize > 1;
                    if (tile.ShownAmountOn != stackable)
                    {
                        tile.ShownAmountOn = stackable;
                        tile.Amount.gameObject.SetActive(stackable);
                    }
                }
            }
        }

        /// <summary>
        /// The quiet frame. Only two things move without the inventory raising Changed: the stack
        /// count (an arrow is loosed) and which quiver is equipped. Both are read off references we
        /// already hold and written only when they differ, so a frame in which nothing happened
        /// costs a few comparisons and allocates nothing at all.
        /// </summary>
        private static void RefreshLive(Player player)
        {
            var equippedAmmo = player != null ? player.GetAmmoItem() : null;   // Humanoid.cs:1992

            for (int i = 0; i < _tiles.Length; i++)
            {
                var tile = _tiles[i];
                if (tile == null || tile.Item == null) continue;
                var item = tile.Item;

                // Vanilla's amount format, verbatim (HotkeyBar.cs:156).
                if (tile.Amount != null && tile.ShownAmountOn && tile.ShownStack != item.m_stack)
                {
                    tile.ShownStack = item.m_stack;
                    tile.Amount.text = item.m_stack + " / " + item.m_shared.m_maxStackSize;
                }

                bool isEquipped = equippedAmmo != null && ReferenceEquals(equippedAmmo, item);
                if (tile.Equipped != null && tile.ShownEquipped != isEquipped)
                {
                    tile.ShownEquipped = isEquipped;
                    tile.Equipped.SetActive(isEquipped);
                }

                float want = isEquipped ? 1f : DimAlpha;
                if (tile.Group != null && !Mathf.Approximately(tile.ShownAlpha, want))
                {
                    tile.ShownAlpha = want;
                    tile.Group.alpha = want;
                }
            }
        }

        // ---- build --------------------------------------------------------------------------------

        private static bool Ensure(Hud hud)
        {
            if (_built && _root != null && ReferenceEquals(_hud, hud) && _builtFor == SlotLayout.AmmoCount)
                return true;

            // The Hud was rebuilt (scene change), or the server changed [Slots] AmmoSlots: start over.
            Destroy();
            if (SlotLayout.AmmoCount <= 0)
            {
                Info("[Slots] AmmoSlots = 0, so there is nothing to show - readout skipped.");
                return false;
            }

            HotkeyBar bar = null;
            GameObject elementPrefab = null;
            RectTransform parent = null;

            if (!Step("find vanilla's hotbar", delegate
            {
                bar = UnityEngine.Object.FindObjectOfType<HotkeyBar>();
                if (bar == null) throw new Exception("no HotkeyBar in the scene");
                elementPrefab = bar.m_elementPrefab;
                if (elementPrefab == null) throw new Exception("HotkeyBar.m_elementPrefab is null");
                _spacing = bar.m_elementSpace > 1f ? bar.m_elementSpace : 70f;
            })) return false;

            if (!Step("find the HUD root to parent onto", delegate
            {
                parent = hud.m_rootObject != null ? hud.m_rootObject.transform as RectTransform : null;
                if (parent == null) throw new Exception("Hud.m_rootObject has no RectTransform");
            })) return false;

            if (!Step("create the readout root", delegate
            {
                _root = new GameObject("NVLB_AmmoHud", typeof(RectTransform), typeof(CanvasGroup));
                _rootRt = (RectTransform)_root.transform;
                _rootRt.SetParent(parent, false);
                _rootRt.anchorMin = Vector2.zero;          // bottom-left of the HUD root, so the
                _rootRt.anchorMax = Vector2.zero;          // offsets mean the same thing at every
                _rootRt.pivot = Vector2.zero;              // resolution and UI scale
                _rootRt.localRotation = Quaternion.identity;
                _rootRt.sizeDelta = new Vector2(_spacing * SlotLayout.AmmoCount, 64f);
                _rootGroup = _root.GetComponent<CanvasGroup>();
                _rootGroup.interactable = false;
                _rootGroup.blocksRaycasts = false;
            })) return false;

            if (!Step("derive the bottom-left anchor from vanilla's own HUD",
                      delegate { _anchor = DeriveAnchor(hud, parent); })) return false;

            if (!Step("clone " + SlotLayout.AmmoCount + " hotbar tile(s)", delegate
            {
                var tiles = new Tile[SlotLayout.AmmoCount];
                for (int i = 0; i < tiles.Length; i++) tiles[i] = BuildTile(elementPrefab, i);
                _tiles = tiles;
            })) return false;

            if (!Step("place the readout", delegate { ApplyLayout(); })) return false;

            _hud = hud;
            _built = true;
            _builtFor = SlotLayout.AmmoCount;
            _visible = true;                    // so the first SetVisible(false) actually fires
            _dirty = true;
            _filled = 0;
            SetVisible(false);

            Info("built " + _tiles.Length + " tile(s) under '" + parent.name +
                 "' from HotkeyBar.m_elementPrefab '" + elementPrefab.name +
                 "' spacing=" + _spacing.ToString("0.#") +
                 "  anchor=" + _anchor.x.ToString("0") + "," + _anchor.y.ToString("0") +
                 " offset=" + Offset.x.ToString("0") + "," + Offset.y.ToString("0") +
                 " scale=" + Scale.ToString("0.##") + " alpha=" + Alpha.ToString("0.##") +
                 "  rect " + UiKit.ScreenRect(_rootRt));
            return true;
        }

        /// <summary>Run one build step inside its own try/catch, naming it in the log either way.</summary>
        private static bool Step(string name, Action body)
        {
            try
            {
                body();
                Info("build step ok: " + name);
                return true;
            }
            catch (Exception e)
            {
                Fail("build step '" + name + "' FAILED: " + e.Message);
                return false;
            }
        }

        private static Tile BuildTile(GameObject prefab, int index)
        {
            var go = UnityEngine.Object.Instantiate(prefab, _rootRt);
            go.name = "NVLB_AmmoTile" + (index + 1);

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) throw new Exception("tile " + (index + 1) + " has no RectTransform");
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(index * _spacing, 0f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;

            var tile = new Tile { Go = go };

            // Vanilla finds these by name; so do we (HotkeyBar.cs:110-118).
            tile.Icon = Child<Image>(go, "icon", index);
            tile.Amount = Child<TMP_Text>(go, "amount", index);
            var equipped = go.transform.Find("equiped");
            tile.Equipped = equipped != null ? equipped.gameObject : null;
            if (tile.Equipped == null)
                Warn("tile " + (index + 1) + " has no 'equiped' marker - the equipped quiver will " +
                     "be shown by its brighter alpha alone");
            else
                tile.Equipped.SetActive(false);

            // Everything a passive readout has no business showing.
            Off(go, "queued");
            Off(go, "selected");
            Off(go, "durability");
            var binding = go.transform.Find("binding");
            if (binding != null)
            {
                var t = binding.GetComponent<TMP_Text>();
                if (t != null) t.text = "";           // blanked the way vanilla blanks it on a gamepad
                binding.gameObject.SetActive(false);
            }

            // A readout must never take a click. The prefab is a Button (HotkeyBar.cs:104) whose
            // persistent listeners survive Instantiate, and every graphic on it is a raycast target.
            var button = go.GetComponent<Button>();
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.interactable = false;
                button.enabled = false;
            }
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            foreach (var t in go.GetComponentsInChildren<UITooltip>(true)) UnityEngine.Object.DestroyImmediate(t);

            tile.Group = go.GetComponent<CanvasGroup>();
            if (tile.Group == null) tile.Group = go.AddComponent<CanvasGroup>();
            tile.Group.interactable = false;
            tile.Group.blocksRaycasts = false;
            tile.Group.alpha = DimAlpha;
            tile.ShownAlpha = DimAlpha;

            if (tile.Icon != null) tile.Icon.gameObject.SetActive(true);
            go.SetActive(false);
            tile.ShownActive = false;
            return tile;
        }

        private static T Child<T>(GameObject go, string name, int index) where T : Component
        {
            var t = go.transform.Find(name);
            var c = t != null ? t.GetComponent<T>() : null;
            if (c == null)
                throw new Exception("tile " + (index + 1) + ": vanilla's hotbar element has no '" +
                                    name + "' child with a " + typeof(T).Name);
            return c;
        }

        private static void Off(GameObject go, string name)
        {
            var t = go.transform.Find(name);
            if (t != null) t.gameObject.SetActive(false);
        }

        // ---- anchoring -----------------------------------------------------------------------------

        /// <summary>
        /// Where the bottom-left of the row goes, measured off vanilla's own widgets rather than
        /// guessed: <c>Hud.m_healthPanel</c> (Hud.cs:110), <c>Hud.m_foodBarRoot</c> (:130) and
        /// <c>Hud.m_gpRoot</c> (:158). Each is converted into the HUD root's own coordinates; the
        /// ones that actually occupy the bottom-left of the screen are kept, and the row is placed
        /// flush with their leftmost edge and <see cref="Gap"/> pixels above the highest of them.
        ///
        /// Measuring rather than hard-coding a constant is what makes the "never overlaps the health
        /// bar, the food icons, the status effects or the minimap" promise hold at any UI scale, any
        /// resolution, and after any vanilla HUD reshuffle - and it is why every candidate's rect is
        /// logged: if the row ever lands somewhere silly, the log says which widget put it there.
        /// </summary>
        private static Vector2 DeriveAnchor(Hud hud, RectTransform parent)
        {
            float pw = parent.rect.width, ph = parent.rect.height;
            float left = float.MaxValue, top = float.MinValue;
            int used = 0;

            used += Consider(parent, hud.m_healthPanel, "m_healthPanel", pw, ph, ref left, ref top);
            used += Consider(parent, hud.m_foodBarRoot, "m_foodBarRoot", pw, ph, ref left, ref top);
            used += Consider(parent, hud.m_gpRoot, "m_gpRoot", pw, ph, ref left, ref top);

            if (used == 0)
            {
                Warn("no vanilla widget was measurable in the bottom-left of the HUD - falling back " +
                     "to " + FallbackAnchor.x + "," + FallbackAnchor.y +
                     " (nudge it with [AmmoHud] OffsetX / OffsetY)");
                return FallbackAnchor;
            }

            var a = new Vector2(left, top + Gap);
            Info("anchor derived from " + used + " vanilla widget(s): left=" + left.ToString("0") +
                 " top=" + top.ToString("0") + " + " + Gap + "px gap -> " +
                 a.x.ToString("0") + "," + a.y.ToString("0") +
                 " (HUD root " + pw.ToString("0") + "x" + ph.ToString("0") + ")");
            return a;
        }

        /// <summary>
        /// Fold one vanilla widget into the anchor, if it really is in the bottom-left. Returns 1
        /// when it counted, 0 otherwise - and logs its measured rect either way.
        /// </summary>
        private static int Consider(RectTransform parent, RectTransform rt, string what,
                                    float pw, float ph, ref float left, ref float top)
        {
            if (rt == null) { Info("anchor candidate " + what + ": absent"); return 0; }

            var corners = new Vector3[4];           // 0 = bottom-left, 1 = top-left, world space
            rt.GetWorldCorners(corners);
            Vector3 bl = parent.InverseTransformPoint(corners[0]);
            Vector3 tl = parent.InverseTransformPoint(corners[1]);

            // InverseTransformPoint answers relative to the parent's PIVOT; anchoredPosition with
            // anchor/pivot (0,0) is relative to the parent's bottom-left CORNER. Convert once.
            float x = bl.x - parent.rect.xMin;
            float yTop = tl.y - parent.rect.yMin;
            float yBottom = bl.y - parent.rect.yMin;

            bool bottomLeft = x < pw * 0.4f && yBottom < ph * 0.5f;
            Info("anchor candidate " + what + ": left=" + x.ToString("0") +
                 " bottom=" + yBottom.ToString("0") + " top=" + yTop.ToString("0") +
                 (bottomLeft ? "  (counts)" : "  (not bottom-left, ignored)"));
            if (!bottomLeft) return 0;

            if (x < left) left = x;
            if (yTop > top) top = yTop;
            return 1;
        }

        private static void ApplyLayout()
        {
            if (_rootRt != null)
            {
                _rootRt.anchoredPosition = _anchor + Offset;
                _rootRt.localScale = new Vector3(Scale, Scale, 1f);
            }
            if (_rootGroup != null) _rootGroup.alpha = Alpha;
        }

        // ---- teardown --------------------------------------------------------------------------------

        private static void Fail(string message)
        {
            _failed = true;
            Warn(message + " - the ammo readout is switched off for this session " +
                 "(the ammo slots themselves are unaffected).");
            Destroy();
        }

        public static void Destroy()
        {
            try { if (_root != null) UnityEngine.Object.Destroy(_root); }
            catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogWarning("[AmmoHud] teardown: " + e.Message); }
            _root = null; _rootRt = null; _rootGroup = null;
            _tiles = new Tile[0];
            _hud = null; _built = false; _visible = false; _builtFor = -1; _filled = 0;
            _anchor = FallbackAnchor;
        }

        /// <summary>Allow a retry after a transient failure (a config toggle, a scene change).</summary>
        public static void Reset()
        {
            Destroy();
            _failed = false;
            _dirty = true;
        }

        public static string Describe()
        {
            return (_failed ? "failed" : _built ? (_visible ? "visible" : "hidden") : "not built") +
                   " tiles=" + _tiles.Length + " showing=" + _filled +
                   " anchor=" + _anchor.x.ToString("0") + "," + _anchor.y.ToString("0") +
                   " offset=" + Offset.x.ToString("0") + "," + Offset.y.ToString("0") +
                   " scale=" + Scale.ToString("0.##") + " alpha=" + Alpha.ToString("0.##");
        }
    }
}
