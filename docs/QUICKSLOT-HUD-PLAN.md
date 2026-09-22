# QuickSlotHud plan (issue #10)

Issue #10: "Would it be possible to add a HUD element showing any additional hotkey buttons with
their keybind and assigned item below the default hotbar when the inventory is closed (ala AzuEPI)?"

Goal: a row of quick-slot tiles under vanilla's hotbar that a player cannot tell from the hotbar
itself. Research only; nothing below is built yet.

## 1. What the competition does

### AzuEPI (AzumattDev/AzuEPI), the "QAB" (Quick Access Bar)

* `Game/Slots/QAB/HudPatches.cs:15-26` postfixes `Hud.Awake`, finds the vanilla hotbar by name
  (`m_rootObject.transform.Find("HotKeyBar")`), `Instantiate`s its whole `RectTransform` into
  `m_rootObject`, renames it `QabName` and puts it at `siblingIndex + 1`. So it is a second real
  `HotkeyBar` component, not a hand-built widget.
* `Game/Slots/QAB/QAB.cs:17-178` prefixes `HotkeyBar.UpdateIcons` and returns `false` for that
  clone, then **re-implements vanilla's whole method body** (element creation, durability flash,
  equipped marker, stack text, the `flag && index == m_selected` gamepad highlight).
* `QAB.cs:419-425` prefixes `HotkeyBar.Update` and returns `false` **unconditionally, for every
  HotkeyBar in the game**, vanilla's included. It then drives icon refresh itself from a
  `Hud.Update` postfix, gated on `Time.frameCount % 2 == 0` (`QAB.cs:270-277`).
* Position: `QAB.SetElementPositions()` (`QAB.cs:216-227`) seeds `QuickAccessLocation` once to
  `healthPanel.anchoredPosition + (-2.5, -380.87)` and writes it into the cfg, then applies it plus
  `QuickAccessScale` as `localScale`. Drag-to-move lives in `HudPatches.cs:62-87`.
* Key label: `Core/Text/SlotText.cs:5-27` stretches the tile's `binding` child to the full tile and
  auto-sizes 10-14, text from `HotkeyTexts[i]` (a free-text override) else
  `Hotkeys[i].Value.ToString()`, the raw BepInEx `KeyboardShortcut`.

Where it falls short of Valheim-like:

1. **Half-rate HUD.** Killing `HotkeyBar.Update` and refreshing on alternate frames (`QAB.cs:270`)
   means the durability flash (vanilla drives it off `Mathf.Sin(Time.time * 10f)`,
   `HotkeyBar.cs:153`), the stack count, the equipped marker and the gamepad selection all update at
   half frame rate, on the **vanilla** hotbar too. Vanilla refreshes every frame (`HotkeyBar.cs:75`).
2. **A forked copy of `UpdateIcons`.** `QAB.cs:51-171` is vanilla's method pasted and edited, so every
   game patch is a chance to drift (issue #39, "breaks with Valheim 1.0").
3. **A magic-number position, frozen into the cfg.** `-380.87` px below the health panel
   (`QAB.cs:224`) has nothing to do with where the hotbar is, and because it is written to config on
   first run it never re-derives when the resolution, the GUI scale or another mod moves the health
   panel. The row is near the hotbar by coincidence, not by construction.
4. **Hand-rolled GUI scale.** The drag hit-test multiplies by `GuiScaler.m_largeGuiScale` and a magic
   `0.375` width factor (`HudPatches.cs:46,69`), so the grab box is wrong off 100% scale.
5. **Per-frame string lookups and writes.** `SetElementPositions` runs every `Hud.Update`
   (`HudPatches.cs:49`), does `Find("hudroot")` plus `Find(QabName)` three times, then writes
   `anchoredPosition` and `localScale` whether or not anything changed (`QAB.cs:218-226`).
6. **Collateral HUD damage and rebuild hacks.** Every MessageHud notification is shoved down 75px
   globally to dodge the bar (`HudPatches.cs:95-103`), and `SettingsMenuClosedRecently`
   (`QAB.cs:6-20, 39-43`) destroys every element after the settings window closes.
7. **The row changes length as you play.** `AlwaysShowQuickSlotsInUI` defaults off, so `amountToShow`
   tracks the highest occupied slot (`QAB.cs:59-71`) and tiles shift sideways when a slot empties.
8. Label text is the raw shortcut string, not a game-rendered bound-key name; renaming a label has
   crashed the game (issue #15), and gamepad bindings collide (issue #32).

### EquipmentAndQuickSlots (RandyKnapp/ValheimMods), `src/QuickSlotsHotBar.cs`

Cleaner idea, same family. `Hud_Awake_CreateQuickSlotsBar` clones the `HotKeyBar` transform, strips
its children and keeps the `HotkeyBar` component. It then **lets vanilla draw**: a prefix on
`Inventory.GetBoundItems` swaps in the quick-slot items only while `HotkeyBar.UpdateIcons` is running
on the quick bar (`HotkeyBar_UpdateIcons_QuickBarScope`), and a postfix rewrites each `binding` text
to `slot.GetShortcutText()`. It also unifies gamepad navigation across both bars so
`JoyHotbarLeft/Right` walks off one bar into the other, and it gates refresh behind a
`HotkeyBarRefreshGate` instead of a frame counter.

Where it falls short: it still prefixes `HotkeyBar.Update` to suppress vanilla for both bars, so the
controller path is a reimplementation; position comes from `ConfigPositionedElement` with an anchor
and offset config, again not derived from the hotbar; and it carries explicit BetterUI and Auga
handoff code because two mods fight over the same bar. Its `Inventory.GetBoundItems` prefix is global
and depends on a `inCall` static staying correct under every exception path (they needed a
`[HarmonyFinalizer]` for exactly that).

## 2. What NVLB already has

* `src/Modules/Slots/AmmoHudView.cs` clones `HotkeyBar.m_elementPrefab` per tile (`BuildTile`, :420)
  and finds leaves by vanilla's own names (`icon`, `amount`, `durability`, `equiped`, `queued`,
  `selected`, `binding`), so a tile is vanilla's widget at vanilla's size in vanilla's font. `Ensure`
  (:316) reads `bar.m_elementSpace` and parents onto `Hud.m_rootObject`. `Step()` (:405) wraps every
  build stage in its own try/catch and logs it.
* 0.10.1 already solved the trap: disabling the prefab's `Button` runs
  `Selectable.InstantClearState()`, which resets the background `CanvasRenderer` tint to white and
  makes the clone a bright grey block. `PaintBackground` (:612) pins the renderer tint and paints the
  colour itself (`thunderstore/CHANGELOG.md`, 0.10.1).
* GUI scale is already free: the root uses `anchorMin/Max = 0`, `pivot = 0` and `anchoredPosition`
  under `Hud.m_rootObject`, so the scaled canvas carries it. No `GuiScaler` math anywhere.
* `ShouldShow` (:202) is AmmoHud's hide-state gate. **QuickSlotHud must not copy it** (see below).
* Quick slots are **not** ZInput buttons. `[Slots] QuickSlotKeys` is a local comma-separated list of
  Unity `KeyCode` names parsed in `ExtraSlotsModule.ParseKeys` (:166-181) and read with
  `ZInput.GetKeyDown(key, false)` in `PlayerUpdatePostfix` (:1146-1155). The label already exists:
  `ExtraSlotsModule.QuickKeyLabel(int)` (:459-467), wired into the panel as `SlotsUi._quickLabel`
  (`SlotsUi.cs:57,346`) and drawn into the cell's own `binding` text by `SlotsUi.Label` (:326-362),
  auto-sized 9-15 in the game's highlight yellow. The HUD row should use the same source and styling.
* Module conventions: `FeatureModule` with `Name`/`Side`/`Section`/`Theme`/`Hint`, `Bind()` using
  `BindSynced`/`BindLocal` with `Opt.N/B/T(...)` (`src/Config/Opt.cs:104-158`), `.As(label)`,
  `.Simple(group, order)`, `.Diag()`; `ApplyPatches()` with `Require(...)` so a missing vanilla
  member throws at load; `Live()` = `Active && ClientActive()`.

## 3. Vanilla, for reference (v1_0_14 dump)

* `HotkeyBar.cs:29-31`: `m_elementPrefab`, `m_elementSpace = 70f`.
* `HotkeyBar.cs:109-131`: one element per bound column at `localPosition = i * m_elementSpace`,
  `binding` text set to `(i+1).ToString()` unless `ZInput.IsGamepadActive()`, then
  `icon`/`durability`/`amount`/`equiped`/`queued`/`selected` cached by name.
* `HotkeyBar.cs:146-174`: durability only when `m_useDurability && m_durability < max`, red flash at
  zero via `Mathf.Sin(Time.time * 10f)`; `m_equiped.SetActive(item.m_equipped)`; amount only when
  `m_maxStackSize > 1`, text `"{stack} / {max}"`, cached against `ElementData.m_stackText` so a quiet
  frame allocates nothing. `:180`: `m_selection` is gamepad-only.
* `HotkeyBar.cs:201-230`: `ToggleBindingHint` blanks and restores every label off
  `ZInput.OnInputLayoutChanged`.
* `HotkeyBar.cs:40`: the long `!InventoryGui.IsVisible() && ... && !Hud.InRadial()` chain gates
  **gamepad input only**. `UpdateIcons` at :75 runs unconditionally, so the vanilla hotbar stays
  drawn with the inventory open, the map open and a store open.
* `Hud.cs:436-464`: `SetVisible` disables nothing, it moves `m_rootObject` to `s_notVisiblePosition`;
  `IsVisible()` is `localPosition.x < 1000f`. `Hud.cs:524` calls
  `SetVisible(!m_userHidden && !localPlayer.InCutscene())`; `m_userHidden` is the Ctrl+F3 toggle
  (`:506-508`). Nothing in `Minimap.cs` touches the HUD root.

**The consequence that shapes this whole design:** anything parented under `Hud.m_rootObject` gets
Ctrl+F3, the gamepad HUD toggle and cutscene hiding for free, and gets exactly the vanilla hotbar's
behaviour everywhere else, without copying a single condition.

## 4. NVLB's design

New module `QuickSlotHud` (`[QuickSlotHud]`), view class `QuickSlotHudView`, built from the
`AmmoHudView` pattern with four deliberate departures.

**Structure.** On first tick, find the vanilla bar with `FindObjectOfType<HotkeyBar>()` (as
`AmmoHudView.Ensure` does), read `m_elementPrefab` and `m_elementSpace`, create a root
`NVLB_QuickSlotHud` (RectTransform + CanvasGroup, `anchorMin/Max/pivot = 0`, non-interactable,
`blocksRaycasts = false`) parented to `hud.m_rootObject` with `SetSiblingIndex` just after the vanilla
bar, and clone one `m_elementPrefab` per quick slot at `anchoredPosition = (i * m_elementSpace, 0)`.

**Position: derived from the hotbar, never a constant.** Convert the vanilla `HotkeyBar` rect's world
corners into the HUD root's frame (the `AmmoHudView.Consider` conversion, :558-583) and place the row
flush with its left edge, one tile height plus a small gap below. Cache the measured rect and
re-derive only when it moves more than half a pixel. This is the biggest improvement over both
competitors: "aligned to the hotbar's left edge, at its exact tile size and spacing" becomes true by
construction at any resolution and any GUI scale, instead of by a tuned offset.

**Per tile, exactly what vanilla draws.** `icon.sprite = item.GetIcon()`; `durability` on the same
`m_useDurability && m_durability < max` condition with the same zero-durability red flash; `amount`
only when `m_maxStackSize > 1`, vanilla's `"{stack} / {max}"` string cached against a per-tile
`ShownStack`; `equiped` lit from `item.m_equipped` using vanilla's own marker. `queued` and `selected`
stay off: equip-queue and gamepad selection belong to the vanilla bar.

**The background.** Improve on AmmoHud: before disabling the prefab's `Button`, read
`button.colors.normalColor` and paint with **that**, rather than AmmoHud's hardcoded
`BackgroundTint`. The tile then reproduces vanilla's tint exactly and follows a reskin. Keep
AmmoHud's renderer-tint pinning and raycast-target stripping.

**Key label.** Reuse `ExtraSlotsModule.QuickKeyLabel(i)` (promote to `internal static`) and style the
`binding` child exactly as `SlotsUi.Label` already does: stretched to the full tile, auto-sized 9-15,
centred, top-aligned, highlight yellow. That puts the key where vanilla puts the 1-8 number, in
vanilla's font, and handles "Mouse4" or "Keypad1" without clipping. `KeyCode.None` gets an empty
label, not a placeholder.

**Empty tiles: shown, dimmed. Recommended.** The row is a keybind map, so a tile must never move. A
collapsing row would slide slot 3's tile under slot 2's position the moment slot 2 empties, breaking
the muscle memory that makes quick slots worth having. Vanilla itself draws empty hotbar columns up
to the highest bound one (`HotkeyBar.cs:98-103, 180-188`), and AzuEPI's collapsing default is the
behaviour its users notice most. Empty tiles keep their label at a lower CanvasGroup alpha;
`ShowEmpty = false` is there for anyone who wants the shorter row.

**Visibility: inherited, not reimplemented.** No `ShouldShow` gate at all. Because the root is a child
of `Hud.m_rootObject`, Ctrl+F3, the gamepad HUD toggle and cutscenes hide it exactly when they hide
the hotbar, and the map, inventory, store and build menu leave it alone exactly as they leave the
hotbar alone. The view switches itself off for only two things, `QuickCount == 0` and gamepad mode.
Strictly less code than AmmoHud's gate, and strictly more faithful.

**Gamepad.** Quick slots are keyboard `KeyCode`s no gamepad can press, so a visible row would be
tiles the player cannot use. Hide the whole root while `ZInput.IsGamepadActive()`, subscribing to
`ZInput.OnInputLayoutChanged` as vanilla does (`HotkeyBar.cs:219-230`) so the switch is immediate
rather than polled. Better than AzuEPI and EAQS, which blank the labels and leave the tiles, though
they earn it back with stick navigation (see open decisions).

**Refresh policy.** A `Hud.Update` postfix like `AmmoHudModule.HudUpdatePostfix`, in try/catch with
the same three-error cap. Rebuild only when a revision tuple changes (`SlotLayout.QuickCount`, a
`KeysRevision` int bumped by `ExtraSlotsModule.ParseKeys`, `ShowEmpty`, the `Hud` instance, the
measured hotbar rect). Contents re-read only on a dirty flag set by `Player.OnInventoryChanged` and
`Player.OnSpawned`, same wiring as AmmoHud. Every other frame compares cached ints, bools and sprite
references and touches Unity only where they differ: no per-frame `Find`, no per-frame
`anchoredPosition` write, no allocation. Every build stage goes through `Step(name, body)`, which logs
and permanently fails the view rather than spamming `Hud.Update`.

### Settings, section `[QuickSlotHud]`

| Setting | Scope | Default | Notes |
| --- | --- | --- | --- |
| `Enabled` | synced | `true` | Framework-bound by `FeatureModule`, same as `[AmmoHud] Enabled`, so a server can switch the row off for everyone. |
| `OffsetX` | local | `0` | Nudge from the derived hotbar-aligned anchor, px. `Opt.N(..., -600, 600, 1)`. |
| `OffsetY` | local | `0` | As above. Positive is up. |
| `Scale` | local | `1` | 1 is exactly hotbar tile size. `Opt.N(..., 0.4, 2.5, 0.05)`. |
| `ShowEmpty` | local | `true` | Dimmed placeholder tiles keep every key in a fixed place. |
| `ShowKeyLabels` | local | `true` | Off draws icons only. |

The brief asked for `Enabled` to be local; the AmmoHud precedent (synced `Enabled`, local
everything-else) is better and is what this plan uses. Flag it if that is wrong.

**Simple view: none of them.** Recommended. The row needs no decision from the player: it appears the
moment `[Slots] QuickSlots` is above 0 and `[Slots] QuickSlotKeys` has a key, and both of those are
already in Simple (`Inventory` order 80 and `Powers` order 50). Adding a seventh Simple row for a
feature with no choice in it is noise. All six stay Advanced, `[QuickSlotHud]`, with `Opt` metadata
and `SettingLevel` exactly as 0.11.x expects.

### Interactions

* **`QuickSlots = 0`.** `[Slots] QuickSlots` is `BindSynced`, so a server can turn it on mid-session.
  Not patching `Hud` at all would mean a restart to see the row. So: patch always, but the postfix
  returns on the first line when `SlotLayout.QuickCount <= 0`, the view never builds, and
  `OnConfigChanged` calls `Destroy()` when the count drops back to 0. This deviates from the brief on
  purpose; the cost is one `if` per frame and the gain is that the server knob works live.
* **AmmoHud.** No stacking. Ammo stays bottom-left, quick slots go under the hotbar (centre-bottom).
  They cannot collide by construction: `AmmoHudView.Consider` only counts widgets whose left edge is
  under 40% of HUD width (`AmmoHudView.cs:558-583`), and the hotbar is centred. Both views log their
  final screen rect, so an overlap caused by a hand-set `OffsetX` is one log line away.
* **The ExtraSlots panel.** It already captions quick slots in the same `binding` text
  (`SlotsUi.cs:326-362`). Sharing `QuickKeyLabel` means panel and HUD can never disagree. The panel
  is only visible with the inventory open; the HUD row stays visible then, exactly as the vanilla
  hotbar does. No suppression, no special case.
* **Vanilla rebinds.** Vanilla's own 1-8 labels are hardcoded `(i+1).ToString()` (`HotkeyBar.cs:119`)
  and do not follow rebinds, so there is nothing to mirror. Our labels follow `[Slots] QuickSlotKeys`
  through `OnConfigChanged` bumping `KeysRevision`.
* **Other mods on the hotbar.** We add a sibling and never patch `HotkeyBar.Update` or
  `HotkeyBar.UpdateIcons`, so AzuEPI or EAQS installed alongside keep working and we keep working.
  Neither competitor can say that of the other.

### Self-test

Add `TestQuickSlotHudPrefab()` to `SlotsSelfTestModule` (client side, at world-ready, same
`Check(bool, string)` style as the existing 27 checks). Against `HotkeyBar.m_elementPrefab`:

* `icon` exists and has an `Image`
* `amount` exists and has a `TMP_Text`
* `durability` exists and has a `GuiBar`
* `equiped`, `queued`, `selected` exist
* `binding` exists and has a `TMP_Text`
* the prefab has a `Button` whose `colors.normalColor` is readable
* `m_elementSpace > 1f`

A game update that renames any of these then fails loudly in `nvlb.selftest` at load, instead of
shipping a blank HUD row that nobody notices until a player complains.

### Work breakdown

| File | Change | Rough lines |
| --- | --- | --- |
| `src/Modules/Slots/QuickSlotHudView.cs` | new; tile clone, hotbar-derived anchor, refresh, paint | ~430 |
| `src/Modules/Slots/QuickSlotHudModule.cs` | new; config, 3 patches, status, enable/disable | ~200 |
| `src/Modules/Slots/ExtraSlotsModule.cs` | `QuickKeyLabel` to `internal static`, add `KeysRevision` | ~12 |
| `src/Modules/Slots/SlotsSelfTestModule.cs` | `TestQuickSlotHudPrefab` | ~45 |
| `docs/MODULES.md`, `README.md`, `thunderstore/CHANGELOG.md` | module count, new section | ~30 |

### Test script for Matt, in game

1. `[Slots] QuickSlots = 4`, `[Slots] QuickSlotKeys = Z,X,C,Mouse4`. Put a torch, a full stack of
   arrows, a worn tool and nothing in the four slots. Close the inventory.
2. Row sits directly under the hotbar, left edges flush, same tile size, font and darkness. The
   `built N tile(s)` and `anchor derived from` log lines should name the hotbar, not a fallback.
3. **Full stack of arrows**: count reads `100 / 100` in vanilla's format and changes as you shoot.
   **Worn tool**: durability bar appears as it wears and flashes red at zero, at the hotbar's rate.
   **Equip the torch**: the `equiped` marker lights exactly as it does on the hotbar.
4. Empty slot 4 shows a dimmed tile labelled `Mouse4` and does not move when slot 3 empties.
5. GUI scale **0.8** then **1.2**: the row stays glued to the hotbar and stays hotbar-sized, with
   nothing nudged by hand.
6. **Ctrl+F3**: row goes and returns with the hotbar on the same frame. **Map, inventory, store,
   build menu**: row stays exactly as long as the vanilla hotbar stays. **Cutscene** (boss altar):
   row goes with the HUD.
7. **Rebind** `[Slots] QuickSlotKeys` `Z` to `Semicolon` in the settings tab: label changes with no
   restart, and the panel caption changes with it.
8. Plug in a **gamepad**: row vanishes; unplug it and it returns.
9. Set `[Slots] QuickSlots = 0` live: row goes. Back to 4: it returns.
10. AmmoHud is still bottom-left and untouched by any of the above.

## Open decisions for Matt

1. **Should quick slots become real ZInput buttons?** Today they are raw `KeyCode`s in
   `[Slots] QuickSlotKeys` (local, comma-separated). Promoting them to `NvlbKeys` declarations would
   put eight rows on vanilla's Keyboard and Mouse page with in-game rebinding and vanilla's
   "key already used" check, and `NvlbKeys.Label` would then be the label source. It is a separate,
   larger change with a config migration, so this plan keeps `QuickKeyLabel`. Worth doing next?
2. **Gamepad: hide, or navigate?** This plan hides the row on a gamepad because the keys are
   unreachable. Both competitors instead extend stick navigation onto the quick bar. Adding that
   means driving `m_selected` ourselves and is a meaningful chunk of work. Hide for now?
3. **Eight quick slots on one row?** At `QuickSlots = 8` the row is as wide as the hotbar. Always one
   row (recommended, it is the shape people expect), or an optional `SlotsPerRow` wrap like AzuEPI's?
4. **Hover tooltip?** Vanilla's hotbar tiles have no tooltip and this plan strips raycast targets so
   the row can never eat a click. Do you want hover-for-name on the quick-slot tiles, accepting that
   the row then becomes a click target?
