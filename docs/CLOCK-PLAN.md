# Clock plan

Source: a Reddit commenter on the NVLB launch post said it "just needs a clock (like text based
one) and mass farming." This plan covers the clock only. Mass farming is out of scope.

## 1. Prior art

- **Clock / ClockMod (aedenthorn)**, https://thunderstore.io/c/valheim/p/aedenthornNexusMods/ClockMod/
  and https://thunderstore.io/c/valheim/p/Pineapple/Clock/versions/ . Shows in-game HH:MM or a
  fuzzy word like "Late Afternoon", plus optionally the day number. Fully custom widget: draggable,
  configurable font/position/size/alignment, hotkey toggle, hides with the HUD. Marked
  **deprecated**, last confirmed fix was for 0.217.14; no 1.0 confirmation. Ships its own look
  rather than borrowing vanilla's, which is the opposite of what Matt wants.
- **ValheimClock (orfox)**, https://old.thunderstore.io/c/valheim/p/orfox/ValheimClock/ . The
  closest match to the ask: sits directly beneath the minimap, shows "Day 32 14:45" (24h or 12h),
  server-synced off `ZNet` so everyone sees the same time, hot-reloads its cfg. Its look is
  deliberately painted (dark panel, gold trim, metal rivets, 5 palette themes) rather than reused
  from vanilla widgets, so it is closer to "a UI I designed to look Viking-ish" than "vanilla's own
  text, reused." Listed under recent Ashlands/Deep North categories, last updated "2 weeks ago" per
  its own page, so functionally current.
- **RealClockMod (aedenthorn)**, same family as ClockMod but shows real-world time instead of game
  time; same deprecation status. Not what Matt asked for (he wants game time, real time is optional
  at most).
- **AzuExtendedPlayerInventory** and general searches turned up no bundled clock; AEPI is an
  inventory/quickslot mod, time display is not part of it.
- No thread or changelog among these surfaced complaints in the fetched text about overlapping the
  minimap specifically, but the shared design instinct across all three (fixed position under or
  near the minimap, toggle, opacity/scale controls) is itself the signal: cohabiting with the
  minimap without stealing its space is the solved problem, and a custom-painted panel is the
  unsolved one for a "vanilla-like" ask.

## 2. Vanilla 1.0.14 internals (from the box dump)

- `EnvMan.GetDayFraction()` (EnvMan.cs:937) returns the current smoothed 0-1 fraction of the day
  cycle. `EnvMan.GetDay()` / `GetDay(double time)` (EnvMan.cs:942-947) give the day number as
  `(int)(time / m_dayLengthSec)`; `GetDay()` calls it with `ZNet.instance.GetTimeSeconds()`, so a
  client can call it directly, no reflection needed.
- The **raw** sun-arc fraction is rescaled once per FixedUpdate (`RescaleDayFraction`,
  EnvMan.cs:296-313) so that raw 0.15 (sunrise) to 0.85 (sunset) maps onto `GetDayFraction()`'s
  0.25 to 0.75 window; outside that, night fills 0.75-1.0 wrapping to 0.0-0.25. This is exactly the
  breakpoint set `IsDay()/IsAfternoon()/IsNight()` (EnvMan.cs:1084-1112) use:
  `IsNight()`: `f < 0.25 || f >= 0.75`. `IsDay()`: `0.25 <= f <= 0.75`. `IsAfternoon()`: `0.5 <= f <=
  0.75` (a subset of day). There is no `IsMorning()`/`IsEvening()` boolean; the closest vanilla gets
  is `OnMorning`/`OnEvening` (EnvMan.cs:407-480), one-shot triggers fired when the smoothed fraction
  crosses 0.25 and 0.75, which post the sleep message `$msg_newday` ("Day N") and swap ambient
  music. Vanilla's four-way lighting/fog/aurora blend (`SetEnv`, EnvMan.cs:833-933) does carry
  `Morning`/`Day`/`Evening`/`Night` weight names, but that is a rendering interpolation, not an
  exposed player-facing state.
- `Minimap.cs` has no day/time text field at all (`m_dayText`, `GetDayNumberText` etc. do not
  exist); the minimap is purely the map image plus pins (`m_smallRoot`/`m_largeRoot`,
  `m_mapImageSmall`/`m_mapImageLarge`, Minimap.cs:146-152). Vanilla ships **no persistent on-screen
  clock or day counter** of any kind; the only day-number text a player ever sees is the transient
  center message on waking.
- `MessageHud.cs` confirms the only day text is that transient message (`m_messageText`, a
  `TMP_Text`, cross-faded in/out, MessageHud.cs:52/104/136/223). There is no dedicated top-left or
  Norse-font clock widget to borrow verbatim; the nearest reusable font/size/colour donor is any
  vanilla `TMP_Text` on `Hud.m_rootObject`, the same donor NVLB already uses for
  `GraveCompassHud` (`Hud.m_gpName`, falling back to `Hud.m_healthText`).
- NVLB's own `AmmoHudView.cs` (`src/Modules/Slots/AmmoHudView.cs`) is the house style for "ship no
  art": clone a vanilla widget (`HotkeyBar.m_elementPrefab`), find its named children
  (`icon`/`amount`/...), strip the interactive bits (`Button`, raycast targets, tooltips), and
  derive the anchor by **measuring** other vanilla HUD rects (`Hud.m_healthPanel`,
  `m_foodBarRoot`, `m_gpRoot`) rather than a hard-coded pixel. `GraveCompassHud.cs` does the same
  with a cloned `TMP_Text` instead of a hotbar tile. The clock should follow the `TMP_Text`-clone
  path, anchored off `Minimap.instance.m_smallRoot`'s `RectTransform` the same way AmmoHud measures
  the bottom-left widgets.

## 3. NVLB conventions read

- `src/FeatureModule.cs`: one module = one file, one Harmony instance, `Side` gate
  (Server/Client/Both), `Bind()`/`ApplyPatches()` contract, `Active` = `Applied && Enabled`, every
  patch body must gate on it. `EnabledOpt` can be `.Simple(group, order)`.
- `src/Modules/Slots/AmmoHudModule.cs` + `AmmoHudView.cs`: `ModuleSide.Client` (a dedicized server
  has no `Hud`), `[Section] Enabled` still bound and synced so the server can force the module off
  everywhere, per-player look knobs (`OffsetX/Y`, `Scale`, `Alpha`) as `BindLocal` so each player
  tunes their own client with no restart, one `Hud.Update()` postfix as the single per-frame hook,
  a static `View` class doing all the Unity work with `_failed`/try-catch-per-step so one bad
  lookup disables only this widget.
- `src/Modules/CorpseRun/GraveCompassHud.cs`: the `TMP_Text`-clone pattern (`Instantiate` an
  existing vanilla `TMP_Text`, re-parent to `Hud.m_rootObject`, explicit anchors since the donor's
  parent is not kept) - the direct template for a text-only clock.
- `src/Config/SimpleGroups.cs`: eight fixed Simple-view headings (`Progression & XP`, `Inventory &
  carrying`, `Powers & hotkeys`, `Crafting from storage`, `Building costs`, `Gathering & the
  world`, `Death & getting back`, `Server & safety`). None is a natural fit for a clock; see open
  decision 2.
- `src/Config/Opt.cs`: `Opt.B` (bool), `Opt.N(hint,min,max,step)` (numeric), `Opt.T` (free text),
  `Opt.C(hint, choices...)` (pick-one), chained with `.Admin()`, `.Restart()`, `.As(label)`,
  `.Simple(group, order)`, `.Diag()`, `.Hidden()`.
- `docs/MODULES.md`: confirms the Enabled-toggle restart rule (`on: restart / off: live`), that
  `BindLocal` never syncs, and that a module's `Theme` places it in the Advanced view by module
  (Client modules commonly use `Theme => "Inventory"` or similar existing groups; `Theme => "World"`
  fits a clock since it is about the game's own day/time state, not gear).

## 4. Recommended design

**Module**: `ClockModule` (`src/Modules/Clock/ClockModule.cs` + `ClockView.cs`), `ModuleSide.Client`,
`Theme => "World"`, `Section => "Clock"`.

**Default look, and why**: plain text, no panel, no background art, positioned directly under the
minimap, in vanilla's own font/colour, reading `Day 42 - Morning` with HH:MM off by default. This
is the most "vanilla-like" of the three prior-art mods because it is not a new widget at all - it
is `Instantiate()`d from an existing vanilla `TMP_Text` (same font asset, outline, base colour as
the rest of the HUD, the same trick `GraveCompassHud` already uses), so it reads as if Iron Gate
shipped it. Text-only avoids ValheimClock's painted panel (rivets, theme picker) and ClockMod's
free-floating draggable widget, both of which announce themselves as a mod. Under the minimap
(not top-centre) because that is the one screen region every prior-art clock converges on and it
is empty in vanilla at every UI scale; `AmmoHudView`'s measure-don't-guess pattern
(`DeriveAnchor`) applies directly to `Minimap.instance.m_smallRoot`.

**Day/time text**: `Day {n}` from `EnvMan.instance.GetDay()`, plus a period word from
`EnvMan.IsNight()` / `IsDay()` / `IsAfternoon()` (EnvMan.cs:1010-1027), since those are the only
period predicates vanilla actually exposes: `IsNight()` -> "Night", `IsDay() && !IsAfternoon()` ->
"Morning", `IsAfternoon()` -> "Evening" (renamed from vanilla's internal "Afternoon" - see open
decision 1). Optional `HH:MM` computed from `GetDayFraction()` (`0.25` = 06:00 sunrise-equivalent
per the rescale in section 2, so `hours = ((f - 0.25) / 0.5 * 24 + 24) % 24`, wall-clock style,
24h only - no AM/PM, vanilla has none). Optional real-world clock (`DateTime.Now`) is a separate,
off-by-default line, since the commenter and Matt both mean game time.

**Update cadence**: recomputed at most once per in-game minute (day length default 1200s / 1440
in-game minutes -> a ~0.83s real-time tick at default settings; the view rounds `GetDayFraction()`
to the nearest minute and only rebuilds the string when that value changes), so no per-frame string
allocation. Mirrors `AmmoHudView`'s dirty-flag discipline.

**Fades and gating**: fade in over the first second after the widget is built or re-shown
(`CanvasGroup.alpha` lerp 0->1, same mechanism `MessageHud` already uses via `CrossFadeAlpha`).
Visibility gate copies `AmmoHudView.ShouldShow` verbatim: `Hud.IsUserHidden()`, `!hud.IsVisible()`
(covers cutscenes), `Minimap.IsOpen()` (the clock should NOT show doubled up while the full map is
open - `Minimap.instance` already draws its own time-of-day tint there), `InventoryGui`/`StoreGui`/
`Menu` open. GUI scale: the widget lives under the same HUD canvas as everything else, so vanilla's
own canvas scaler handles it for free; `Scale` is an extra multiplier on top, exactly like
AmmoHud's `Scale`.

## 5. Settings (`[Clock]`, machine-local except `Enabled`)

| Key | Type | Default | Sync | Notes |
|---|---|---|---|---|
| Enabled | bool | **true** | synced | On by default: the commenter wants it out of the box, and it is inert text under an empty screen region, unlike a gameplay change. Server can still force off. |
| Format | choice: `Text`, `Text+Time`, `Time` | `Text` | local | `Text` = "Day 42 - Morning"; `Text+Time` appends " 14:07"; `Time` drops the word, HH:MM only. |
| ShowRealTime | bool | false | local | Appends the OS clock on its own line. Off by default per section 4. |
| Position | choice: `BelowMinimap`, `TopCenter`, `BottomLeft` | `BelowMinimap` | local | `BelowMinimap` measured off `Minimap.m_smallRoot`; the other two are fixed-anchor fallbacks for a UI mod that moves the minimap. |
| OffsetX / OffsetY | float | 0 | local | Same nudge pattern as `[AmmoHud] OffsetX/OffsetY`. |
| Scale | float, 0.5-2.0 | 1.0 | local | Multiplier on the cloned donor's own font size. |
| Opacity | float, 0.1-1.0 | 0.85 | local | Whole-widget `CanvasGroup.alpha` ceiling (the fade-in lerps up to this, not to 1). |

**Simple view**: `Enabled` and `Format` under a **new** Simple group is the honest answer - none of
the eight existing `SimpleGroups` fits a HUD toggle (`World` isn't one of the eight; `Server &
safety` is closest but wrong audience). Recommend adding `SimpleGroups.Hud = "HUD & display"` (a
ninth heading) since a clock is the first HUD-cosmetics setting to reach Simple and more will
likely follow (compass, future widgets). If Matt would rather not grow the list, second choice is
folding `Enabled` only into `Server & safety` under "Achievement-safe" style read-only framing -
worse fit, no code cost. This is open decision 2.

## 6. Self-test

Pure function, no Unity: `ClockFormat.Describe(float dayFraction, int day, ClockFormat format)` -
feed synthetic fractions (0.0, 0.24, 0.25, 0.49, 0.5, 0.74, 0.75, 0.99) and confirm the period word
and HH:MM string at each boundary, plus day-number formatting for day 0 and a multi-digit day.
Wired into `nvlb.catalog selftest` style headless check the way `SafeSlotsSelfTest` and
`[PickerSelfTest]` already run at world load, so a boundary regression fails loudly on a dedicated
server even though the module itself is client-only.

## 7. Work breakdown

- `src/Modules/Clock/ClockModule.cs` (~110 lines): `FeatureModule` subclass, config binds, one
  `Hud.Update()` postfix, mirrors `AmmoHudModule.cs` structure.
- `src/Modules/Clock/ClockView.cs` (~180 lines): the `TMP_Text` clone, anchor derivation off
  `Minimap.m_smallRoot`, fade-in `CanvasGroup`, per-minute dirty check. Mirrors
  `AmmoHudView.cs`/`GraveCompassHud.cs`.
- `src/Modules/Clock/ClockFormat.cs` (~60 lines): the pure formatting functions the self-test
  exercises, kept separate from `ClockView` exactly as `AmmoHudView.Collect` is kept pure for its
  own self-test.
- `src/Modules/Clock/ClockSelfTest.cs` (~50 lines): the boundary table from section 6.
- `src/Config/SimpleGroups.cs`: +1 line if the new `Hud` group is approved (open decision 2).
- `docs/MODULES.md`: +1 row.

## 8. Matt's in-game test script

1. Fresh spawn: confirm "Day 1 - Morning" appears under the minimap within a second, faded in, not
   overlapping the map circle at 100% and at the smallest/largest GUI scale.
2. Toggle HUD hidden (Ctrl+F3 or gamepad) - clock disappears with everything else; open the full
   map - clock hides while the map is open, reappears on close.
3. Set `Format = Time`, confirm HH:MM only, no day/period word; set `Text+Time`, confirm both.
4. Sleep through a full night, confirm the day number increments and the period word walks
   Night -> Morning -> Evening -> Night without ever flashing the wrong word at a boundary.
5. Try `Position = TopCenter` and `BottomLeft`, confirm no overlap with health/food/minimap at
   those spots; nudge with OffsetX/Y.
6. Drop `Opacity` to 0.1 and back to 1.0 live (no restart), confirm it takes effect immediately -
   `BindLocal` should hot-reload the same as `AmmoHud`'s knobs do.

## 9. Open decisions

1. **Word for the second half of the day.** Vanilla's own boolean is named `IsAfternoon()`, but
   "Evening" reads better on a clock right before night falls. Recommend "Evening"; if Matt wants
   the code-accurate word instead, it is a one-line string swap.
2. **A ninth `SimpleGroups` heading ("HUD & display") vs. reusing an existing one.** Recommend
   adding it (section 5); costs one line and one precedent for future HUD toggles, but grows the
   Simple view's group count for the first time since it shipped.
3. **Default `Enabled = true` out of the box.** Matches the commenter's ask directly, but it is the
   first *visual, always-on* addition to the screen in NVLB (AmmoHud only shows with ammo in the
   new slots; the compass only shows with a tracked grave) - worth Matt's explicit yes rather than
   assuming it.
