
# Changelog — NoVikingLeftBehind

## 0.10.0 (2026-09-09) — BuildersGuild: cheaper building at the base (38th module)

New module **BuildersGuild** `[Builders]` — build big at your base without turning costs off. Three factors, all live-editable,
composed into the one build-cost pipeline the tier and settlement discounts already use (shown, checked, consumed and refunded
from the same number, rounded once):
* **Yard** — a material is cheaper while its home station is within `YardRadius` (80 m): wood/core wood/fine wood near a
  workbench, stone near a stonecutter, iron/bronze/copper near a forge, black metal near a black forge (`Materials`, default
  ×0.5, iron ×0.10). Outside every yard the multiplier is 1.0. Optional `StationLevelScaling`, per-material `MinAmounts`.
* **Framing** — every beam and pole (43 pieces on 1.0, incl. the new Timber set; `StructuralPieces` to add/remove) costs a flat
  1 of each material free-standing and **0 when snapped onto another beam or pole**. Each piece records what it was paid for
  on its own network data, so deconstruct refunds exactly that — never more. A 5-high iron support tower: 1 iron + 1 wood.
* **Builder skill** — a per-character level 0–100 tracked by NVLB (no death penalty, invisible to vanilla), 1 XP per vanilla
  material of every piece placed, up to `SkillMaxDiscount` (30%) at level 100. `nvlb.builder` shows it.
* **Rhythm** — placing the same piece again within `RhythmWindowSec` (20 s) builds a streak: 5% per repeat, capped at 25%.
* The hammer's piece info shows the breakdown, e.g. `Yard -50% Stone · Builder Lv 31 -9% · Rhythm x4 -20%`.
* Refund rule for all transient factors: a refund uses the cheapest price the piece could have been built for (yard applied,
  full rhythm), so tearing something down never pays out more than it cost. Crafting recipes, station costs, repair and wards
  are untouched. Headless self-test: 31 checks.

## 0.9.2 (2026-09-09)

* **Vanilla "Stack all" / hold-E no longer empties the extra slots.** Valheim's `Inventory.StackAll` walks the whole
  player inventory with no row filter, so stacking into a chest that held arrows or food pulled them straight out of the
  ammo and food slots (equipment survived only because it is flagged equipped). A one-instruction transpiler now feeds
  StackAll only the vanilla rows when the inventory is NVLB-managed; everything else about the button is vanilla,
  unmanaged inventories are untouched, and the self-test replays the guard over the live game IL on every server boot
  so a future game patch that moves the call is caught early. New synced `[Slots] StackAllProtectsSlots` (default on;
  off = vanilla behaviour). SlotsSelfTest: 102 passed.

## 0.9.1 (2026-09-09)

* **Settings tab showed stale values after a save.** On a client, ServerSync answers "serialize this setting" with the
  machine's own pre-sync cfg value whenever the config is locked, so every non-toggle row (sliders, text, lists, keys,
  cyclers) redrew the client's old number after a successful save — `[Chests] Range` saved as 50 on the server, shown
  as 20 for ever. Rows now read the value actually in force, and a saved value stays displayed until the server's
  echo lands. A `[SettingsMenu] row [...] queued/sent/synced/live/shown` log line names the stale source if it recurs.
* **CorpseRunPlus tracks the richest grave, not the newest.** Up to 5 unlooted graves are remembered per world with
  their item counts; the compass, grave pull and portal follow the one with the most items (ties → most recent), so a
  second death carrying two mushrooms no longer hides a 30-item grave. Looting a grave removes that grave (matched by
  tombstone id, else nearest) and tracking falls through to the next best. Empty deaths record nothing; new synced
  `[CorpseRun] MinItemsToTrack` (default 1) filters the rest. Compass reads `Grave · 31 items · 240 m` with a "+N more"
  hint; new rebindable "Next grave" key (`[CorpseRun] CycleGraveKey`, default unbound) and console `nvlb.grave.next`,
  `nvlb.grave.list`, `nvlb.grave.clear all`. Old single-grave records migrate. Self-test: 21 passed.

## 0.9.0 (2026-09-09) — Valheim 1.0

**Rebuilt for Valheim 1.0.7 (network version 39).** Requires BepInExPack_Valheim 5.4.2350. Not compatible
with 0.221.x — stay on 0.8.8 if your server is held on the `default_pre1_0` branch. Server and clients must
match (0.9.0 rejects older clients, as always).

* **Extra slots survive 1.0's new inventory-rows upgrade.** 1.0 added a vanilla "more rows" upgrade whose
  `Player.SetInventorySize` runs on every login and then drops on the ground every item outside the vanilla
  grid — which would have been the entire extra-slot area. NVLB now restores its grid height before that
  scan and again after the resize, so vanilla only drops genuinely invalid items. If a player takes vanilla's
  upgrade beyond 4 rows the panel geometry overlaps the bigger bag; the log says so loudly (layout for that
  is a follow-up).
* Ported to 1.0 signatures: `InventoryGrid.Element` → `InventoryElement`; the 14-argument load-path
  `Inventory.AddItem`; `Inventory.Changed(bool, bool)`; `Inventory.IsTeleportable(bool allowAllItems)`
  (PortalTrail now also honours 1.0's hard block on `m_toolTier >= 1000` and never relaxes it);
  `SEMan.AddStatusEffect(..., short variant)`; `SaveSystem.GetCharacterFolderPath`; `Terminal.ConsoleCommand`'s
  new `hideBehindDevCommands`; `Inventory.AddItem(..., bool cheated)` in the test commands.
* Every patch target re-verified against the 1.0 decompile; the FoodNoDecay transpiler assertion holds at
  its true count; all headless self-tests pass (SlotsSelfTest 88/88, SafeSlots 26/26, Pickers 19/19,
  CorpseRun 7/7).

## 0.8.8 (2026-09-08)

A pre-launch safety pass over the code, looking only for things that could crash the game, hang it,
or lose an item. Two were found, both in the extra-slots plumbing, and both are guards rather than
changes — nothing about how the mod plays is different in this version.

- **An extra-slot item can no longer be lost when there is nowhere to put it.** When the extra slots
  have to be emptied — you turned the module off, the server changed the slot counts, a rescue ran at
  login — each item is taken out of its cell and then given a home: a free bag slot first, the ground
  at your feet second. If your bag was completely full *and* the drop failed (which is possible at
  login, before the world is fully awake), the item had already been taken out of the inventory and
  was simply gone — while the log said it was safe in the save blob, which for an item that came out
  of that blob was not true. Such an item is now kept in your inventory instead, and the log says
  plainly that it was kept. Nothing is ever destroyed for want of a slot.
- **A corrupt extra-slots blob can no longer take the game down on login.** The saved blob starts with
  a count of how many items it holds, and that number was trusted: a corrupted character file could
  claim two billion items and the game would try to reserve room for all of them before reading a
  single one — a hang or an outright close, on the character-load path, with no message. The count and
  the per-item custom-data count are now sanity-checked (512 and 256, against a real layout of about
  30 slots), and a blob that fails the check is refused the same way a version-mismatched one already
  was, so your backups are tried instead. The same check covers a blob arriving from the server's
  SafeSlots vault.
- Docs: the module count is corrected to **37** in the README and the Thunderstore listing (the
  in-game log has said 37 since SafeSlots landed; the docs still said 36).

## 0.8.7 (2026-09-08)
- **Turning the extra slots off and on again no longer empties them.** This is the fix for "my extra-slot
  items got moved back into my inventory". Switching `[Slots] Enabled` **off** deliberately moves every
  extra-slot item into your bag — leaving items in cells that are about to stop existing is how items get
  deleted, so that part is right and stays. But switching it back **on** only regrew the grid; the items
  stayed loose in your bag and the slots stayed empty. Toggling that switch twice in the settings tab — six
  seconds apart, which is exactly what happened (toggled by hand while testing the settings tab) — therefore
  emptied your extra slots for good, and the next
  save honestly recorded "nothing in the slots", pushing the real list down into backup 1. Nothing was ever
  lost. Turning the module back on now puts each item back in the slot it came from.
- **If it already happened to you, the game now tells you how to undo it.** Log in and, if your extra slots
  are empty while a backup still holds items, you get: *"Your extra slots are empty but backup 1 still holds
  5 items. Type nvlb.slots.restore 1 to put them back."* It is never automatic — slots you emptied on
  purpose stay empty.
- **`nvlb.slots.restore` can no longer give you a second copy.** If the items a backup lists are already in your
  inventory — which is exactly where the bug above put them — the command says so and restores nothing,
  instead of handing you duplicates of everything. Only items you genuinely no longer carry come back.
- **And a save can no longer record fewer items than you are carrying.** The save blob keys each item by its
  slot and quietly skipped any item whose cell had no slot in the current layout — which is what a server
  changing a `[Slots]` count does to a client that is already holding things. Such a save is now refused
  outright, with the offending items and cells named in the log, rather than writing a short list over a good
  one. The same guard covers a save attempted mid-lift.
- Every evacuation is now logged at warning level with the item, the cell it came from and the reason, and
  every load logs what the blob and all three backups hold before a single item is placed — the two things
  that were missing when this had to be diagnosed after the fact.
- Eleven new headless checks, including the exact five items from the character this happened to.

## 0.8.6 (2026-09-08)
- **Fixes 0.8.5 closing the game.** Opening a module that owns one of the new list settings —
  CorpseRunPlus is the one most people would reach first — froze Valheim and then shut it down
  outright, with no error, no crash report and a log that simply stopped mid-sentence. 0.8.5's
  picker asked the row builder for a plain text field to hide behind its **Raw** button, but the
  row builder is the thing that decides a list setting should get a picker, so it handed the row
  straight back: picker, builder, picker, builder, until the stack ran out. An overflow like that
  cannot be caught — the process just ends, which is why there was nothing to read afterwards.
  The picker now asks for the field itself rather than going back through the decision.
- **And makes the next one findable.** Every row is named in the log *before* it is built and
  timed after, so a build that dies leaves the offending row as the last line rather than nothing
  at all; a rebuild that somehow starts while one is already running is refused and logged instead
  of recursing; and the drop-down arrows can no longer step through an empty list of choices.

## 0.8.5 (2026-09-08)
**Tick the ore off a list instead of knowing its name.** `[Mining] OreNodes` reads
`rock4_copper:1,MineRock_Tin:1,silvervein:3` and there was no way to edit that without already
knowing every prefab name in the game, which is a fair description of nobody. **Nineteen** list
settings now open a picker instead: search, tick what you want, set the number beside it, OK.
Things are listed by their in-game name — *Copper deposit*, not `rock4_copper` — with the prefab
underneath, read out of the world you are actually playing, so whatever other mods have added is
in the list too. A **Raw** button brings the text field back for anyone who would rather type.

- **Nothing is lost.** An entry already in the value that this world does not have — a prefab from
  a mod that is not installed here — is listed first, still ticked, and flagged *not found in this
  world*, so editing from the wrong machine cannot quietly delete another server's settings. Order
  is preserved too: `[CorpseRun] RespawnFoods` is a "best first" list, and picking from it no
  longer reshuffles it. A headless self-test proves the round trip is byte-identical for every one
  of the nineteen settings before any of this ships.
- **The network preset can reach FastLink.** The arrows on every drop-down row stepped from the
  value on the server rather than the one on screen, so each click went to the same neighbour and
  the third choice could never be reached. That affected every drop-down in the tab, not just this one.
- **The `<` and `>` arrows have arrows on them again**, along with every other button whose caption
  could vanish — the same fault the footer buttons had, swept across the whole page this time.
- **The network rows explain themselves in plain words.** They were showing SmoothServer's own
  config-file descriptions, which for the preset is a wall of `[SendCadence] SendHz=60,
  [AdaptiveBudget] CeilingBytes=262144 ...`. Each of the five now says what it does and when you
  would want it.

## 0.8.4 (2026-09-08)
- **One place decides what a hotkey is.** Changing a hotkey in the config did nothing — and you did not have to
  rebind anything for that to happen. Valheim writes every rebindable key into your saved settings the moment you
  close the Settings window with **OK**, so the first time anyone did that, whatever the defaults happened to be
  at that moment were frozen — and from then on the saved copy was re-applied over the config on every load. The
  config was dead and nothing said so. Now the saved binding is the single answer, the config value is the
  default it starts from, and this mod remembers which default a saved binding was frozen from: a binding that is
  merely a stale copy of a default we have since changed moves with it, while a key you actually chose is never
  touched. That is what left one tester being told to hold Alt while Ctrl was still what worked. Setting a key in
  our own tab now writes the binding itself, which is what it should have been doing all along.
- **Clicking a setting's name stopped dragging its slider.** The click was never landing on the name: a cloned
  vanilla slider brings the whole width of the settings row it came from, so a piece of the slider was stretched
  out underneath the label, and a slider answers a press anywhere on itself by jumping to wherever that press
  maps — off the left end of the track, that means the minimum. The slider now catches clicks on its track and
  nowhere else.
- **The module checkboxes work again.** 0.8.3 shrank their clickable area to the box itself, which was right, and
  then hung the row's tooltip on that very patch — and a tooltip answers a click before anything underneath it
  can. The tooltip moved one level up, where it shares an object with the checkbox instead of covering it, so the
  box ticks and the rest of the row still selects the module.
- **Quick slots stop eating keys they were never given.** With `[Slots] QuickSlots = 0` the quick-slot keys were
  still being swallowed, and the default list was `Z,X,C` — `X` is vanilla's Sit. Nothing is consumed at zero,
  only the first *N* keys are ever honoured, and the default list is now **empty**: give quick slots keys of your
  own before they do anything.
- **One loadout out of the box.** `[Loadouts] Slots` now defaults to **1** and `Loadout2Key` to none; set `Slots`
  to 2 and give the second one a key when you want it. Anyone still on the old defaults is moved across.

## 0.8.3 (2026-09-08)
- **Closes the last way a typed value could vanish.** 0.8.2 picks up anything typed once the caret leaves the
  field — but the right-hand pane is torn down and rebuilt from scratch on *every keystroke in the search box*
  and on every click in the module list, and Unity destroys a field without ever telling it that it lost focus.
  So typing a value and then reaching for Search, or for another module, still threw it away. Every open field is
  now read **before** the pane is rebuilt, and again when the tab closes, which depends on nothing arriving in
  time. This was the exact path that swallowed a `Loadout1Slots` edit outright: no write, no error, nothing.

## 0.8.2 (2026-09-08)
**Nothing in the settings tab happens until you say so.** Until now every click, every nudge of a slider and
every mis-click went straight to the server. The tab now **queues**: an edit marks its row — tinted, with a
bullet — and waits. The footer counts them (**Save changes (3)**) and there is a **Discard** beside it that puts
every control back to what is actually in force. Save sends them through the same door, one at a time, in the
order you made them, so the audit and Undo still read change by change. Vanilla's own **OK** saves the queue and
closes; **Back** and **Escape** stop and ask — Save, Discard or Cancel — instead of quietly throwing your work
away. The module checkboxes down the left queue too, and so do your own machine-local settings.

- **Keys are recorded, not typed.** Every key setting is now a button showing its binding: click it, it says
  *Press a key…*, and it takes the next key or combination you press. Escape cancels, a small **×** clears it.
  This replaces a text field that was a trap — one tester typed `p`, which is a perfectly reasonable thing to
  type, and the config ended up holding the lowercase `p` that Unity's key parser refuses, so the loadout key
  was simply dead with nothing said about it. Key parsing is now case- and space-insensitive as well
  (`p`, `F1`, `left alt` all work), and an unparseable key says so in the log instead of vanishing.
- **A typed value can no longer go missing.** A text field only reported itself on Enter or on losing focus, and
  on this page focus can move to another row without either ever happening — so a value typed and left was never
  saved at all. Anything typed is now picked up when the caret leaves, when Save is pressed, and on OK.
- **Quotes stopped multiplying.** `1,2` typed with quotes was stored as `"1,2"`, then `\"1,2\"`, then
  `\\"1,2\\"` — the config writer escaped the value again on every save, until nothing could parse it. Strings
  are stored raw now, and one layer of stray quotes is peeled off whatever is read, so an already-mangled config
  repairs itself. Slot lists accept `1,2`, `1, 2`, `"1,2"` and `'1,2'` alike.
- **Clicking a module stopped toggling it.** A cloned checkbox answered clicks across the whole row; it now has
  one small hit patch over the box itself, on both the module list and the settings rows. And the section
  headings — **CATCHING UP**, **INVENTORY** — are buttons now: they light up under the pointer, and clicking one
  jumps the list to that section and opens its first module.
- **`[Loadouts] SaveModifier` now defaults to LeftAlt.** It was LeftControl, which is vanilla's crouch, so
  storing a loadout meant crouching. Anyone still on the old default is moved across automatically; anything you
  set yourself is left alone. The "loadout is empty" message names the keys you actually have bound, too.

## 0.8.1 (2026-09-08)
- **The loadout key no longer fights the game.** `Loadout1Key` was **V**, which is vanilla's own
  auto-pickup toggle — so one press did both. It is **Z** now (unbound in vanilla, as is `B` for
  loadout 2). If your config still holds the old default it is moved for you, once, with a line in the
  log; a key you chose yourself is left exactly as it is.
- **Rebind this mod's hotkeys on Valheim's own Keyboard & Mouse page.** Loadout 1, Loadout 2, the
  save-loadout modifier, the second power slot, the craft-from-chests toggle, repair-all and
  dismiss-grave are registered as real Valheim keybindings, so they can be rebound where every other
  key in the game is rebound — and the rebind is saved by the game itself, alongside vanilla's. The
  config entries are now just the **defaults**. Anything that clashes with a key vanilla already
  owns is called out once in the log rather than silently double-firing.
- **A loadout can be two hotbar slots instead of a saved pair.** Set `Loadout1Slots="1,2"` and the
  key equips whatever is sitting in hotbar slots 1 and 2 at the moment you press it — main hand
  first, then off-hand. Nothing is stored, so there is nothing to keep in step: rearrange the hotbar
  and the loadout follows. An empty slot is left alone rather than emptying that hand, a two-handed
  weapon in the first slot takes both hands, and the save modifier does nothing for a loadout in this
  mode because there is nothing to save. Leave it empty for the original behaviour — both modes share
  the same storage, so switching back finds your saved pair intact.
- Docs: the readme's boss-altar paragraph still described the pre-0.8.0 mapping. Corrected.

## 0.8.0 (2026-09-08)
- **Nobody has to back up their extra-slot items any more.** New module **SafeSlots** (35 modules -> **36**),
  on by default, three parts and nothing to configure.
- **Your character file is copied before this mod first touches it.** On a character's first login with
  NoVikingLeftBehind — and again any time items are found parked in an older extra-slots mod's layout — the
  `.fch` is copied to `characters_local\nvlb-backups\<name>-<date-time>.fch`, keeping the newest three per
  character. Both copies are taken from inside the load itself, which is the last instant at which the file on
  disk is still exactly what it was before the mod existed. The path comes from the game's own profile, so it
  follows the game if a future update moves the save folder; a cloud save (no local file to copy) logs one line
  and is skipped rather than failing.
- **A receipt when items are moved.** Migrating off shudnal's ExtraSlots used to be silent unless you read the
  log. You now get one message — `Moved N items from your old extra slots` — and a second, distinct one naming
  anything there was genuinely no room for, so you know to pick it up. The message and the log line are built
  from the same record, so they cannot disagree.
- **And a copy on the server.** After every character save the client sends the same block of extra-slot items
  it writes into your character to the server, which keeps the five newest versions per character under
  `config/nvlb/vault/`. Unchanged items are never uploaded, uploads are spaced at least 30 seconds apart, logging
  out always uploads, and anything over 64 KB is refused with a log line rather than written. The vault is keyed
  on the character's own permanent id, not on your account, so each of your vikings has its own and renaming one
  cannot lose it.
- **If you ever log in to empty slots, the game tells you they are safe.** When — and only when — your extra
  slots come up completely empty and nothing was rescued, and the server does hold items for that character, you
  get: `Your server vault holds N extra-slot items (saved 2 hours ago). Type nvlb.slots.vault restore to get them
  back.` Nothing is ever restored behind your back: slots you emptied on purpose stay empty. `nvlb.slots.vault`
  lists what the server has; `nvlb.slots.vault restore [n]` puts a version back through exactly the same path as
  `nvlb.slots.restore` — its own slot first, then any slot that fits, then your bag, and the ground only if the
  bag is full. Items already where they belong are recognised, so restoring twice says *already present, nothing
  to do* instead of duplicating anything. The server refuses a vault that is not the character you are playing.
  `[SafeSlots] SelfTest` proves the whole store on a headless server: 26 checks, including a byte-for-byte file
  round trip, the version cap, the size guard and one byte under it.
- **Boss altars work the way round you expect now.** Pressing E at a guardian stone used to fill whichever slot
  happened to be empty, and holding Shift was what forced slot 1 — backwards. From 0.8.0: **E on its own always
  sets slot 1**, exactly like vanilla; **Shift+E sets slot 2**; **Ctrl+E sets slot 3** when you run three slots.
  The stone's own tooltip now says so — a line under vanilla's `[E] Activate power`, in the same style, naming
  your actual configured keys, telling you which power it would replace, or that the slot already holds this one.
  You no longer have to keep the modifier held for the two seconds the stone takes to charge: the slot is decided
  the moment you press. A power can only live in one slot, so it is taken out of its old one. With the module off
  or `Slots=1` this is all exactly vanilla — no extra line, no re-routing, no messages. `SecondSlotModifier`
  (LeftShift) and `ThirdSlotModifier` (LeftControl) replace `Slot1Modifier`, which is now ignored; your existing
  config still loads and says so once in the log if you had changed it.
- **The settings tab: clicking a module's name selects it.** Only the blank strip between the name and the
  checkbox used to work — the name itself was a dead click, because 0.7.5 made every label swallow presses so a
  click on a setting's name could not fall through onto the slider behind it. That swallowing is still exactly
  right for the settings pane and is untouched there; the module list's labels now hand the swallowed click to
  the row instead. The checkbox stays its own control and does not select. The selected module also **looks**
  selected now — a faint bar behind its row and a brighter name — and the hover tooltips are unchanged.

## 0.7.6 (2026-09-08)
- **Fixes 0.7.5's blank page.** Putting a tooltip on the module names — one of 0.7.5's own fixes — threw on the
  very first row and took the whole tab down with it. The hover needs something the pointer can hit, and it added
  an invisible `Image` when it found none; but `UnityEngine.UI.Graphic` forbids two of its kind on one object and
  that rule covers the whole family, so adding an `Image` beside a text component quietly returns **nothing** —
  and the next line dereferenced it. It now uses whatever graphic is already on the object and only adds one when
  there is genuinely none.
- **And makes that class of mistake cost one row.** Every module row and every settings row is now built inside
  its own guard: one that fails logs itself, by name, and the rest of the page carries on. The catch around the
  whole build is still there, but it is a backstop now rather than the only thing standing between a typo and an
  empty tab.

## 0.7.5 (2026-09-08)
- **The mouse wheel works.** Both panes took about twenty notches to move two pixels. Unity's ScrollRect scrolls
  by whatever the game's input module hands it, and Valheim scales the wheel by 0.15 before anyone sees it, so a
  notch was worth almost nothing. The tab now reads the wheel itself and uses only its **sign**: one notch, one
  row, in whichever pane the pointer is over, however the value arrives. No inertia, clamped at both ends, and
  each pane has a **slim indicator** down its right edge that sizes itself to how much is off-screen and hides
  when everything fits — 39 modules do not fit, and nothing said so.
- **Clicking a setting's name no longer moves its slider.** Clicking the "Window days" label sent the slider to
  its minimum: the label's hover area had no click handler of its own, so the press went looking for one and
  found the control. The hover areas now swallow presses outright — a click on a name or a hint is a dead click —
  and a label can never be widened into the control column, however narrow the page.
- **The footer buttons are back inside the page, with their captions.** "Undo last change" and "Reset module to
  defaults" were being placed 24px *below* the page, on top of vanilla's own Back row, and were blank: the
  explicit caption size introduced in 0.7.4 was being pinned into the donor's own caption rect, which is barely
  taller than its auto-sized text, and TMP draws nothing when the text does not fit. The caption is now given the
  whole button to sit in, and the text is set last.
- **The module list has tooltips again**, on the name, the row and the checkbox alike — the checkbox sits on top
  of its corner of the row, so hovering it used to reach nothing — and they say more: the module's one-liner, the
  full description of its on/off switch, whether turning it **on** needs a restart (turning it off never does),
  its theme, its section, which side it runs on and its state on this machine. Theme headings also get some air
  above them, so a heading reads as starting a group rather than trailing the row above.

## 0.7.4 (2026-09-08)
- **The settings tab is readable.** 0.7.3 put the tab on screen and the door worked, but half the text on it was
  missing: no setting names, no hints, no module names, no search placeholder — a column of bare toggles and
  sliders — while a giant tooltip sat pinned in the corner of the screen. All of it was **one** cause. The page
  borrows its font from a real vanilla label, and it borrowed it from the first one in the hierarchy: the big
  **"Settings" heading**, about 34px. Every label was then built at 0.72–0.95 of *that* — 25 to 32px — inside rows
  22 to 28px tall, and TMP set to ellipsis draws **nothing at all** when even the first line will not fit. The
  labels that did show were exactly the ones whose rows happened to be tall enough. The font donor is now the
  **median** of the page's ordinary labels — the body text, which one big heading cannot skew — and the size is
  clamped to 14–22px; labels are single-line so a too-large font truncates sideways instead of vanishing, and a
  row label can never be squeezed below 120px wide however narrow the page. The tooltip follows the cursor
  properly (it was mixing pivot-relative coordinates with a corner anchor, which is what pinned it top-left),
  caps its font, and steps aside rather than covering the search box; the footer buttons match everything else
  instead of auto-shrinking to 11px; and the audit strip cuts each line at 60 characters with the full text on hover.
- **New: `nvlb.uidump`.** A console command that dumps every label on the built page — text, active, enabled, font
  size, alpha, wrap and overflow mode, rect size, screen corners, sibling index, parent, and any ancestor
  CanvasGroup fading it — plus the page and both panes. Open the tab, then run it. A label that does not draw is
  nearly always one of those numbers, and a screenshot cannot tell you which.

## 0.7.3 (2026-09-08)
- **The settings tab, actually.** 0.7.2's new logging did its job on the first try and named the culprit in one
  line: the settings menu's tab list has **seven** entries, not six — the six you can see plus a hidden seventh
  that has a page and no button at all (a platform tab the game does not draw on PC). The tab was cloned from
  "the last tab in the list", which was that one, so it found no button to copy and gave up — silently on 0.7.1,
  and four times over with a reason on 0.7.2. It now walks backwards to the last tab that has **both** a button
  and a page, clones that, and drops the new button in beside it; the step between buttons is measured between
  two tabs that actually have buttons, since a buttonless entry has no position to measure from. Our own tab is
  still appended at the end of the list, so vanilla's tab snapshot stays lined up index for index — its tab
  switching already skips buttonless entries. The donor's name and index are logged.

## 0.7.2 (2026-09-08)
Three fixes from a live test session.

- **The settings tab now turns up.** On 0.7.1 the NoVikingLeftBehind tab did not appear in Valheim's Settings
  menu for one tester — and, worse, left nothing in the log either way, so there was nothing to go on but a
  screenshot of six vanilla tabs. 0.7.2 fixes both halves of that. It no longer hangs everything on the single
  `Settings.Awake` prefix: **four hooks** now call the same idempotent install — the `Settings.Awake` prefix, a
  `Settings.Awake` postfix, a `TabHandler.Init` prefix, and postfixes on the two places the game instantiates
  the settings screen (`Menu.OnSettings` in game, `FejdStartup.OnButtonSettings` on the main menu). Whichever
  fires first wins and the rest find the page already there and do nothing, so there is still exactly one tab.
  A hook that arrives *after* vanilla has snapshotted its tab list re-runs vanilla's own `SetAvailableTabs()`
  by reflection, so the snapshot still lines up with the tab bar index for index, and then initialises our page
  by hand. And it is now **loud**: patch time logs the exact method Harmony rewrote (declaring type, assembly,
  IL size), each hook logs that it fired, every refusal logs the value that caused it — no TabHandler, an empty
  `m_tabs`, an unusable donor tab — the new button logs its parent, active state, position and size, and
  the first hook of each open lists every mod that has patched `Settings.Awake`, because Harmony skips the
  remaining prefixes once one of them returns false. If the tab still hides, the log now says why.
- **Two power slots, side by side, both with their ring.** `[Powers] HudOffsetX` / `HudOffsetY` now default to
  **84 / 0** instead of 0 / -56: the second Forsaken power sits next to the first instead of underneath it,
  where the two names ran into each other. And changing that offset no longer costs the second slot its circle
  mask and charge ring — slot 2 was coming back a bare square icon while slot 1 stayed round. The clone maps the
  widget's parts by component *index*, so it has to copy a pristine tree; the ring's teardown used Unity's
  `Object.Destroy`, which is deferred to the end of the frame, so the decoration was in fact still hanging in the
  tree at the moment of the copy. It is unparented before it is destroyed now, the clone is swept for leftovers,
  and a live offset edit re-checks the decoration on the spot instead of hoping the next frame notices — with a
  line in the log (`[Powers] HUD rebuilt (offset 84,0) -> re-decorated slots=2 icon=50px`) so it is visible.
- **The grave compass's dismiss hint is quieter.** "Hold Delete to dismiss grave" was drawn in the same bold
  white as the distance above it. It is now dimmer and smaller — and adjustable: new machine-local
  **`[CorpseRun] HintAlpha=0.55`** (0–1, fraction of the distance label's opacity) and **`HintScale=0.8`**
  (0.5–1.2, fraction of its font size), both live, both re-applied on every compass tick.

## 0.7.1 (2026-09-08)
**A Network panel in the settings tab.** If your group also runs [SmoothServer](https://thunderstore.io/c/valheim/p/Nosferatu/SmoothServer/),
the NoVikingLeftBehind tab now has one more entry — **Network (SmoothServer)** — with the handful of its switches
that actually fix a bad afternoon. It appears only when SmoothServer is installed, and it is deliberately five rows
and not its sixty knobs: the rest stay in its config file, where they belong.

- **The five rows.** *Network preset* (`[Profiles] Profile` — `Default` / `FastLink` / `Custom`), *Compression*
  (`[Compression] Enabled`), *Shared map* (`[Map] Enabled`), *Smooth motion (this PC)* (`[SmoothMotion] Enabled`,
  written to your own machine's config and nobody else's), and the admin-only *Require the client mod*
  (`[General] EnforceClientMod`). Each has the same one-line hint under its name and SmoothServer's own full
  description on hover. A **Reset network to Default** button under them puts the preset back to `Default` with
  compression and the shared map on, after asking you to confirm.
- **The door writes a second config file.** A change to one of these goes through exactly the machinery every other
  setting does — permission, tier, value validation, the rate limit, a timestamped `.bak-` backup, the undo history,
  the chat line and the server log — and then writes `Nosferatu.SmoothServer.cfg` in the same directory. SmoothServer's
  own file watcher and its own ServerSync take it from there, so a preset flip re-tunes the whole server live and
  everyone's client follows. The audit line names the switch the way the menu does: `[NVLB] Erik set Network preset
  FastLink -> Default`.
- **Five keys, by name, and nothing else.** The server validates every incoming SmoothServer change against a
  hand-written allowlist (section, key, type, permitted values, tier). Anything not on it is refused however it is
  asked for, including by an admin — the panel can never become a remote control for that mod's other settings. If
  SmoothServer is not installed on the server, the door answers "SmoothServer is not installed on this server".
- **No dependency either way.** There is no compile-time reference to SmoothServer: it is found at runtime through
  BepInEx's plugin list and read through its own live config entries. Without it this release behaves exactly like
  0.7.0. SmoothServer itself is unchanged — nothing in it is patched, wrapped or reimplemented.
- **Refusals that tell you the truth.** SmoothServer's preset re-asserts its own table over the config file after
  every reload, so switching compression off while the preset owns it would be silently undone a second later. The
  door refuses that up front and says to set the preset to `Custom` first. A SmoothServer module that booted disabled
  is answered with "Needs a server restart", read from its own module list.
- **New: `[Access] NetworkSelfTest`** (admin, off by default). On a dedicated server it drives the whole thing once at
  world load — flip the preset and back, re-read SmoothServer's file from disk each time, undo both, round-trip a key
  in a second section, and prove the three refusals — then restores everything. 235 settings -> 236; still 35 modules.

## 0.7.0 (2026-09-08)
**The settings menu** — a NoVikingLeftBehind tab inside Valheim's own Settings screen, usable by everyone on the
server, that changes any hot-reloadable setting live. Until now only whoever had a shell on the server box could
change anything, which is not much use to friends playing while the owner is at work. 33 modules -> **35 modules**;
every one of the **235 settings** now describes itself. Nothing about the config file changes: `cfg.py`, the
timestamped backups and hand edits all keep working exactly as before.

- **Every setting declares itself** (`src/Config/Opt.cs`, `src/Config/ConfigCatalog.cs`). The two bind helpers
  every module already goes through, `BindSynced` / `BindLocal`, take an optional `Opt` carrying a **hint**
  (<= 12 words, plain language, what it affects), a **range** (min/max/step) for numbers, **choices** for pick-one
  strings, a **tier** (who may change it) and **live** (false where a value is only read once at boot).
  `FeatureModule` gained `Theme` and `Hint` so each module has a group and a one-liner too. The result is a
  `ConfigCatalog` that the settings tab, the server's validation and the new `nvlb.catalog` console command all
  read, so the three can never disagree. Counts on a fresh server: 235 settings, 183 synced / 52 local, 193
  Everyone / 42 admin-only, 224 live / 11 restart-only.
  Ranges are deliberately **not** handed to BepInEx as `AcceptableValueRange`: BepInEx clamps to an acceptable
  range when it loads a file, which would silently rewrite a server's existing cfg the first time a range here
  turned out to be too tight. They are advisory for the UI and enforced only at the door, where a refusal is
  visible and reversible.
- **Access — new module** (`src/Access/AccessModule.cs`, `[Access]`, Side `Both`). One door, on the server, that
  every change goes through. A client sends `NVLB_Tweak(section, key, value)` over `ZRoutedRpc`; the server checks
  `[Access] TweakAccess` (`Everyone` by default), the per-setting tier against `adminlist.txt`, that the setting is
  known and live, parses and validates the value against the catalog, rate-limits the sender
  (`MaxChangesPer10s=10`), and then **writes the cfg file on disk** with a timestamped `.bak-` backup in exactly
  the format `cfg.py` uses — so the existing file watcher, `Config.Reload()` and ServerSync push it to everyone
  through the path that already existed. The cfg file stays the one source of truth; the mod does not grow a
  second config system. Every change is announced once to the whole server (`Announce=Chat` by default,
  `Message` / `Both` / `Off` available) and written to the server log, and the server keeps a 20-deep-per-key undo
  history behind `NVLB_Undo`. `NVLB_ResetModule` puts a whole section back to its defaults under a single backup.
  Local (per-player) settings never reach the server at all — the tab writes the client's own cfg.
  `[Access] SelfTest=true` makes a dedicated server drive the entire door once at world load — change a setting,
  re-read the file from disk to prove it changed, hold for 45 seconds, undo, re-read again — and log every step.
  Admin-only by default: `[ServerKeys]` (every key), `[General] EnforceClientMod` / `Mode` / `HotReload`,
  `[Debug] AllowTestCommands`, every `SelfTest` / `DryRun`, and `[Access]` itself. `[Frontier] TierOverride` and
  `[Tiers] MaterialTiers` are deliberately left open to everyone — players may move their own frontier.
- **SettingsMenu — new module** (`src/Ui/SettingsMenuModule.cs`, `[SettingsMenu]`, Side `Client`). A
  **NoVikingLeftBehind** tab in Valheim's Settings screen, from the pause menu in game and from the main menu.
  Left column: every module grouped by theme, each with its `Enabled` toggle inline. Right column: the selected
  module's settings, one row each — label, the hint underneath, a control (toggle / slider with a typed number
  box / pick-one / text field), a reset-to-default button, and the full description on hover. A search box at the
  top filters label, key, hint and description across all 235 settings. The header says who may change things and
  shows the last five changes anyone made; the footer has Undo and Reset-module. Values move under you when
  somebody else changes something, because the row redraws on `SettingChanged` rather than on a click. A row you
  may not change is greyed with the reason — "Needs a server restart", "Only a server admin can change this",
  "Join a server to change this" — rather than hidden, so everyone can see what the mod can do
  (`[SettingsMenu] ShowUnavailable=false` hides them instead).
  The hook is a **prefix** on `Settings.Awake`, not a postfix: `Awake` calls `InitializeTabs()`, which snapshots
  the tab list into a private list it later indexes by tab number, so a tab added afterwards would index out of
  range. Added first, with a component implementing `Valheim.SettingsGui.ISettingsTab` on its page, vanilla drives
  the tab exactly as it drives Gameplay or Audio.
  **No art is shipped.** Every control is a runtime clone of a vanilla one found on the other settings pages, so
  the tab inherits the game's fonts, colours, parchment and UI scale — and keeps inheriting them through a
  reskin. Clones have their prefab-authored UnityEvents replaced and their page-bound tooltips stripped, because
  `Instantiate` keeps persistent listeners whose targets live outside the copied subtree.
- **`nvlb.catalog`** — new console command next to `nvlb.status`: every setting with its hint, type, range,
  permission tier and whether it applies live, optionally filtered by text. A dedicated server, which has no
  terminal, writes the same content to `nvlb-catalog.tsv` beside its cfg at boot.

## 0.6.0 (2026-09-08)
**Building & gathering** — four new modules for the half of Valheim that is a construction game: tools that tear
through material they outclass, a workbench whose reach grows with your settlement, cheaper building as the clan
grows, and half-weight materials next to the bench. 29 modules -> **33 modules**.

- **OverkillTools — new module** (`src/Modules/Building/OverkillToolsModule.cs`, `[Tools]`, Side `Both`).
  A hit on a tree, log, stump, rock or plain destructible is scaled by
  `min(1 + PerTierBonus x (hit.m_toolTier - target.m_minToolTier), MaxMultiplier)` — a black metal axe fells a birch in
  a couple of swings, an iron pickaxe bites a boulder harder than an antler one, and **day-one Meadows is untouched**,
  because a stone axe on a beech has a tier gap of 0 and gets exactly vanilla numbers. Defaults `PerTierBonus=0.5`,
  `MaxMultiplier=3.0`, `AffectTrees/AffectRocks/AffectDestructibles=true`.
  Hooks are the five owner-side handlers that already gate on tool tier — `TreeBase.RPC_Damage`, `TreeLog.RPC_Damage`,
  `MineRock.RPC_Hit`, `MineRock5.RPC_Damage`, `Destructible.RPC_Damage` — and the whole hit is scaled with
  `HitData.DamageTypes.Modify(float)`, so a chop hit scales its chop and a pickaxe hit its pickaxe with no per-damage-
  type special cases. **Deterministic by construction:** the formula reads only the target prefab's own
  `m_minToolTier` and the hit's serialised `m_toolTier`, so it is identical whichever machine owns the object — which
  is why the module is `Both` and its patch bodies deliberately do *not* gate on the running side.
  A bonus needs a player's tool: `HitType.Structural` is rejected outright (`MineRock5.CheckSupport` manufactures a
  `m_toolTier=100` hit to collapse unsupported areas), a `Player` attacker always qualifies, any other `Character`
  never does, and an attacker whose object is not instantiated on this machine qualifies only when `m_toolTier > 0`.
  **No double-dipping with FastMining:** every prefab in `[Mining] OreNodes` — fractured stages included — is skipped,
  whether or not FastMining is enabled, so copper, tin, silver, obsidian and meteorite behave exactly as they did in
  0.5.1. Creatures, players and `WearNTear` build pieces are never patched at all.
  `[Tools] SelfTest=true` logs a table of 26 real prefabs with their component family and `m_minToolTier` and the
  multiplier at tool tiers 0–4.
- **WorkbenchReach — new module** (`src/Modules/Building/WorkbenchReachModule.cs`, `[Workbench]`, Side `Both`).
  `effective range = vanilla + PerTierMetres x world tier + PerLevelMetres x (level-1)`, hard-capped and never below
  vanilla. Defaults `PerTierMetres=2`, `PerLevelMetres=6`, `MaxRangeMetres=60`, `Stations="piece_workbench"`. On the
  test world (tier 3) that is **26 m at level 1, 46 m at level 3 and 60 m (capped) at level 5**, against vanilla's 20 m.
  One postfix on `CraftingStation.GetStationBuildRange()` moves every range check together — placement
  (`Player.HaveRequirements(Piece, RequirementMode)`), deconstruct **and** hammer repair (`Player.CheckCanRemovePiece`),
  the build HUD's workbench row (`Hud.SetupPieceInfo`) and CraftFromChests' own build check all go through the same
  static — so the HUD, the validation and the consume path cannot disagree. `m_rangeBuild` lives on the shared prefab
  and is **never mutated**: only the return value is rewritten, per instance, per call. A postfix on
  `CraftingStation.ShowAreaMarker()` grows the hover circle to match, so you can see the reach you have.
  **The monster-spawn suppression area is deliberately NOT enlarged** — vanilla sizes that `EffectArea.PlayerBase`
  collider from the same field this module never writes, so it stays exactly vanilla.
- **SettlementDiscount — new module** (`src/Modules/Building/SettlementDiscountModule.cs`, `[Settlement]`, Side
  `Client`). Build pieces cost `max(1 - PerTierDiscount x world tier, 1 - MaxDiscount)` — 10% per boss down to a floor
  of half price, so a three-boss world pays x0.70 and a wood door costs 3 wood instead of 4. Defaults
  `PerTierDiscount=0.10`, `MaxDiscount=0.50`, `MinAmount=1`, plus optional `ExcludePieces` and `OnlyCategories`.
  **Build pieces only** — crafting recipes cost exactly what they always cost. The factor is composed into
  TrailingTierDiscount's existing requirement-scaling context (`tierMult x stationMult x settlementFactor`, rounded
  once), rather than added as a second `GetAmount` postfix that would round twice; it keeps its own section and its own
  `Enabled` so it can be toggled live and independently, but the shared machinery is installed by `[Discount]`, which
  must therefore be enabled at startup.
- **Deconstruct refunds can no longer exceed the price** (`Piece.DropResources(HitData)`, new prefix + finalizer in
  `TrailingTierDiscountModule`). Vanilla's refund path reads `requirement.m_amount` **directly**, never `GetAmount()`,
  so any build-piece discount used to let you build a wall cheap and break it for full price, over and over. Both the
  cost and the refund now go through one function — `ScaledAmount(amount, PieceCostFactor(piece))` — so they are equal
  by construction. This also closes the same hole for the tier discount, which has been open since 0.3.0. One
  documented consequence: a piece built before a discount applied refunds today's cheaper price, deliberately erring
  against the player.
- **BuildersLoad — new module** (`src/Modules/Building/BuildersLoadModule.cs`, `[Load]`, Side `Client`). Inside a
  bench's build range, listed building materials weigh `WeightMultiplier` (0.5) — 60 wood drops from 120 to 60, 60 iron
  from 720 to 360 — so a big wall is a couple of trips instead of ten. Defaults `Stations="piece_workbench,piece_stonecutter"`,
  `HysteresisSeconds=3`, `CheckIntervalSeconds=0.5`, and 19 materials, every one of them resolved against 0.221.13's
  ObjectDB before shipping. One postfix on `Inventory.GetTotalWeight()`, the single accessor every encumbrance and
  weight readout goes through, and it returns immediately unless the inventory *is* the local player's own — chests,
  carts, ships and other players are structurally out of reach. Range comes from WorkbenchReach's shared helper, so
  "near a bench" means exactly what the build hammer means by it. **Hysteresis** keeps you "in range" for 3 s after
  walking out, so the encumbrance arrow cannot flicker on the boundary. The carry-weight **cap** is never touched and
  nothing is stored: walk away and the weight comes straight back.
- **Docs.** New "Building" theme section in both READMEs with four `Tune it:` lines, four new rows in
  `docs/MODULES.md` (33 modules + `Tiers` = 34 rows), and the module count updated everywhere it is stated.

## 0.5.1 (2026-09-08)
**Per-station recipe costs** — TrailingTierDiscount can now re-price every recipe made at a named crafting station,
whatever its tier. Still 29 modules; no new module.

- **`[Discount] StationMultipliers = "BCA_CookingPot:0.75"` — new setting** (`src/Modules/Economy/TrailingTierDiscountModule.cs`).
  Comma-separated `StationPrefabName:multiplier` pairs. Every recipe whose `m_craftingStation` prefab name is listed
  costs that fraction of its ingredients at **every quality level** — in the crafting panel, in the "can I craft this?"
  check and in what actually leaves your inventory. Matching is on the **prefab** name
  (`recipe.m_craftingStation.gameObject.name`), never the localised `m_name`; recipes with no crafting station, and
  build pieces, are never matched. Multipliers are clamped to `0.01..1` (this module still never makes anything more
  expensive) and compose with the tier discount by multiplication, with `MinAmount` still the floor.
  **Why:** CookingAdditions rewrites its own "Crafting Costs" back to its defaults on every server boot, so its soups,
  salted meats and coated eggs cannot be re-priced from its config at all. The default entry takes that mod's custom
  cooking pot to three-quarter price; vanilla stations are untouched unless you list them.
- **The number shown, checked and consumed still cannot disagree.** The station factor rides the same thread-static
  context as the tier factor and is applied in the same one place (the `Piece.Requirement.GetAmount(int)` postfix), so
  nothing in `ObjectDB` is mutated — deliberately: another mod's ItemManager re-applies its own numbers over the shared
  recipe data, and editing `Recipe.m_resources` would be a fight you lose on the next boot. One new context site,
  `InventoryGui.DoCrafting(Player)`, publishes `m_craftRecipe`'s station around the craft, because the
  `Piece.Requirement[]` handed to `Player.ConsumeResources` cannot say which station it came from.
- **Proof in the log.** `[Discount] station multipliers: BCA_CookingPot x0.75 (10 recipes matched)` is written once per
  world load — from the `ZoneSystem.Start` hook, the first point `ObjectDB.m_recipes` is populated on **both** halves —
  and again on every live edit of the setting, on a dedicated server too (where this client-side module is otherwise
  `disabled(side)`). The match count is in `nvlb.status`, and `[Economy] SelfTest=true` now prints five matched recipes
  ingredient by ingredient with their before/after amounts.

## 0.5.0 (2026-09-08)
**Crew sailing** — two modules that make a boat with people on it feel different from a boat with one person on it,
without making anything faster. 29 modules now.

- **SeaLegs — new module** (`src/Modules/Crew/SeaLegsModule.cs`, `[SeaLegs]`, side Both).
  *With a crew aboard, your longship points closer into the wind — you can tack where a lone sailor must row.*
  **Cone narrowing only. Nothing gets faster.** Vanilla's `Ship.GetWindAngleFactor()` is
  `num = Vector3.Dot(EnvMan.instance.GetWindDir(), -transform.forward)`;
  `num2 = Mathf.Lerp(0.7f, 1f, 1f - Utils.Abs(num))`;
  `num3 = 1f - Utils.LerpStep(0.75f, 0.8f, num)`; `return num2 * num3`
  (with `Utils.LerpStep(l,h,v) = Clamp01((v - l) / (h - l))`). The postfix **recomputes that expression verbatim and
  replaces only the LerpStep pair** — `num2` (the off-wind floor: 1.0 beam-on, falling to 0.70 dead upwind and dead
  downwind), `m_sailForceFactor` and `Speed.Full` are never touched, so downwind speed is bit-identical to vanilla at
  every crew size. Crew 1 (or 0) returns without writing the result at all.
  The pair comes from a cone in **degrees**: `hi = cos(deg)`, `lo = hi - 0.05` (vanilla's own ramp width). Defaults
  32° / 27° / 23° for 2 / 3 / 4+ aboard against vanilla's 36.87°, i.e. `LerpStep(0.7980, 0.8480)` /
  `(0.8410, 0.8910)` / `(0.8705, 0.9205)`. Degrees are clamped to **[20, 36.87]**: the dead zone can never close
  (straight upwind is still oars) and can never be made wider than vanilla, and the table is forced monotonic so more
  crew can never point worse than fewer.
  **Crew count comes from the ship's ZDO, never from `m_players.Count` on a reader.** `Ship.m_players` is filled by
  the local physics scene (`OnTriggerEnter`/`OnTriggerExit`), so at a zone edge two clients legitimately hold
  different lists and anything derived per client would desync. The ship **owner** — the one machine that also runs
  `Ship.CustomFixedUpdate`, which early-returns on everyone else — writes the count (helmsman included, clamped 0–8)
  into ZDO int `nvlb_crew` from a postfix on `Ship.UpdateOwner`, which vanilla already runs on an
  `InvokeRepeating("UpdateOwner", 2f, 2f)` started in `Ship.Start`, and only when the number actually changed. Everyone
  (owner included) reads it back in the wind-angle postfix, so the owner's sail force and the passenger's sail
  animation agree.
  **Visual: the minimum.** No sounds, no messages, no vignette — vanilla already lerps `Hud.m_shipWindIcon`'s colour by
  `GetWindAngleFactor()`, so the helmsman simply sees the wind arrow brighten when the crew makes the sail catch. The
  one addition is a postfix on `Hud.UpdateShipHud(Player, float)` that gives **passengers** that same read-only gauge —
  `m_shipWindIndicatorRoot` / `m_shipWindIconRoot` / `m_shipWindIcon` driven from the ship they are standing on, with
  the rudder and sail controls hidden (local `PassengerWindGauge=true`). It restores the vanilla HUD unconditionally on
  every frame it is not showing the gauge — including the frame you take the tiller — and only tracks the two objects
  vanilla never re-activates itself.
  `[SeaLegs] SelfTest=true` (local) logs the whole cone table at load — the LerpStep pair and resulting degrees for
  crew 1..4, plus the beam-on and dead-downwind invariants — pure arithmetic, so a headless server can check it.
  Settings: `Crew2Cone=32`, `Crew3Cone=27`, `Crew4Cone=23`, `MaxCrewCounted=4`, local `PassengerWindGauge=true`,
  local `SelfTest=false`.

- **Lookout — new module** (`src/Modules/Crew/LookoutModule.cs`, `[Lookout]`, client-side).
  *Stand off the tiller on a moving ship and you see further — the map reveals wider for the lookout.*
  Extra map-fog radius for the local player and **nothing else**: no broadcast line, no automatic pins, no serpent
  callout, no voyage stat, never `ExploreAll`. Completely client-local — no ZDO, no RPC, no shared state — so two
  clients with different settings (or one with none) can never disagree about anything.
  Hook: prefix + postfix on `Minimap.UpdateExplore(float dt, Player player)`, whose vanilla body reads the radius
  straight off `m_exploreRadius`; the prefix scales that field for the duration of the one call and the postfix puts
  back the exact float it saved. A postfix that called `Explore()` a second time was rejected — `UpdateExplore` runs
  every frame while only the timer decides whether the O(r²) pixel loop actually fires. Three guards against a leaked
  scale: the restore is unconditional (not gated on `Active`), the prefix self-heals a value left behind by a call that
  never reached its postfix, and `m_exploreRadius` is read from exactly one place in the whole game.
  The helmsman (`player.GetControlledShip() != null`) always gets the vanilla radius. No "stand at the bow" rule and no
  role system: anyone aboard who is not steering is the lookout.
  Settings: `RadiusMultiplier=1.75` (clamp 1.0–3.0, 1.0 = off), `RequireMoving=true`, `MinSpeed=1.0`.

- Docs: `docs/MODULES.md` gains the two rows (29 modules + `Tiers` = 30 rows), both READMEs gain an
  **On the water** section.

## 0.4.9 (2026-09-08)
- **RepairAll — new module** (`src/Modules/Repair/RepairAllModule.cs`, `[Repair]`, client-side; 27 modules now).
  Open a workbench, forge, artisan table or any other crafting station and **every item in your inventory that
  this station is allowed to repair is repaired in one go** — no more clicking the little hammer once per
  damaged item.
  Vanilla's rules are not relaxed anywhere. Eligibility is decided by calling vanilla's OWN private
  `InventoryGui.CanRepair(ItemDrop.ItemData)` directly (visible at compile time through the assembly
  publicizer) rather than a re-implementation that could drift from it, and the candidate list is vanilla's
  own `Inventory.GetWornItems()` ("uses durability AND below max"). So repairs stay **free**, a forge still
  cannot repair a workbench item, a station below the recipe's `m_minStationLevel` still refuses, and each
  item is healed exactly the way `InventoryGui.RepairOneItem()` heals it — `RaiseSkill(Crafting, 1 -
  durability/max)` then `m_durability = GetMaxDurability()`. Equipped items are repaired, as in vanilla.
  One **station repair effect** (`CraftingStation.m_repairItemDoneEffects`, the same EffectList vanilla plays
  per item) and one **top-left message** per batch — "Repaired 7 items", via vanilla's own `$msg_repaired`
  localisation — never per-item spam. The repair button is left dead and un-glowing straight after the batch,
  exactly as `OnRepairPressed`'s follow-up `UpdateRepair()` would have left it.
  Hook: a postfix on **`InventoryGui.UpdateRepair()`** — the only vanilla path that has already resolved the
  station (`Player.m_localPlayer.GetCurrentCraftingStation()`; `InventoryGui.Show(Container)` has not), and the
  one `InventoryGui.Update()` calls every frame while the GUI is visible, so a single hook covers both "the
  station GUI just opened" and "the repair panel refreshed while it is open", and samples the hotkey.
  The batch arms once and disarms after firing. It re-arms on `InventoryGui.Hide()` (postfix), on losing the
  current station, and when the player inventory's item **count** changes (you dragged a damaged item out of a
  chest) — deliberately *not* on durability, because an equipped torch drains every frame and a naive
  "repair whatever is repairable" would machine-gun the repair sound at a workbench. A 0.25 s floor between
  batches backs that up, and any throw in this per-frame GUI path logs once and disables the module for the
  session rather than spamming the log.
  NVLB's **ExtraSlots are real cells of the player's own `Inventory`**, not a second container, so gear parked
  in an equipment / utility / generic slot is covered automatically, with no extra code. Building pieces and
  the hammer are deliberately **out of scope** — nothing standing in the world is ever touched.
  New settings: `[Repair] Enabled=true`, `Trigger="OnOpen"` (`OnOpen` | `Hotkey` | `Both`) and
  `ShowMessage=true`, all server-synced; local `Hotkey="R"` (Unity KeyCode name, or `None`) and
  local `SelfTest=false`, which flips the module's side to Both so a dedicated server logs every repairable
  item in `ObjectDB` grouped by the station and station level vanilla would demand — a headless proof of the
  eligibility rules with no client and no game state touched. Turning `[Repair] Enabled` off is live.

## 0.4.8 (2026-09-08)
- **DualPowers / CombatRecharge — the Forsaken-power HUD, upgraded** (`src/Modules/Powers/PowerRing.cs`, new).
  CombatRecharge already turns a fight into cooldown recovery, but vanilla shows that as a square icon and a
  shrinking number, so nobody could see it happening. Every power icon — vanilla's slot 1 *and* DualPowers'
  cloned slot 2/3 — is now drawn as a **circle** with a thin **charge ring** around its edge that fills as the
  cooldown recovers: full ring = ready, and every second a landed or taken hit shaves off makes the ring jump
  forward. One soft pulse at the moment the power comes ready, nothing animating while idle.
  Still **no shipped UI assets**: both sprites (an antialiased disc for the circle mask, an annulus for the ring)
  are generated at runtime into a 256×256 RGBA32 `Texture2D` and cached for the process. The ring is a plain
  Unity `Image` with `type=Filled` / `fillMethod=Radial360` / `fillOrigin=Top`, `fillAmount = 1 - remaining/total`,
  where *total* is the power's own `StatusEffect.m_cooldown` (times `CooldownMultiplier`), never a hardcoded
  20 minutes — the ring self-corrects if the remaining time ever exceeds it.
  The vanilla icon's sprite and colour are never replaced: the icon object is moved inside a generated
  circle `Mask` and put back — parent, sibling index, anchors, pivot, size — the moment the decoration is torn
  down. Because PowerHud maps the cloned widget's leaves by component index, it now un-decorates before cloning
  and the ring re-applies on the same frame, so the clone can never inherit or duplicate the decoration.
  New, all **machine-local** (cosmetic, per player, never server-synced) and all live on a config edit:
  `[Powers] RoundIcons=true`, `CooldownRing=true`, `RingThickness=4` (pixels, 1–24), `RingColor="E6C88AD9"`
  (Valheim's warm parchment gold at 85% alpha) and `RingTrackColor="00000066"` for the un-filled remainder.
  `RoundIcons` and `CooldownRing` are independent — either alone works.
  Robustness, because this runs inside `Hud.UpdateGuardianPower` every frame: one throw anywhere logs once,
  tears the decoration down and permanently disables it (the powers themselves keep working); a widget that has
  not been laid out yet is retried instead of failing; a non-square icon rect uses the smaller side; a Hud
  rebuild, `ShowHud` toggle or player respawn re-decorates lazily. A client logs
  `[Powers] HUD ring: decorated slots=… icon=…px ring=…px …` once when it builds. `[Powers] SelfTest=true` now
  also proves the disc/annulus coverage maths and the sprite generation headlessly on a dedicated server.

## 0.4.7 (2026-09-07)
- Docs only: the mod page now leads with what it is — the ultimate Valheim quality-of-life mod — with the catch-up
  story as the "why" underneath. No code change. Version bumped so Thunderstore takes the new page; the server's
  version check means everyone updates to 0.4.7 together (r2modman → Update, or import the zip).

## 0.4.6 (2026-09-07)
- **FistsAndShields** `[Fists]` (new module): fist weapons can be used with a shield. Vanilla declares every fist weapon
  `ItemType.TwoHandedWeapon`, and `Humanoid.EquipItem` branches on that type, so equipping fists drops your shield and
  equipping a shield drops your fists — even though bare fists (which are not an item at all) have always worked with a
  shield, which is the proof that the unarmed animation set is shield-compatible. The module flips one field per item
  prefab in `ObjectDB` — `m_shared.m_itemType` `TwoHandedWeapon` → `OneHandedWeapon` — for everything whose skill is
  `Unarmed`, so vanilla's Flesh Rippers and every modded fist weapon (Hugo's Armory / Shapekeys_and_More leather, deer,
  bronze, iron, silver and black-metal fists) are covered automatically; `[Fists] ExtraPrefabs` / `ExcludePrefabs` adjust
  the list by prefab name or item name. `SharedData` is per-prefab, so items already in a chest or on a character pick it
  up with no migration.
  What the change does and does not touch, from a grep of the whole decompile (`TwoHandedWeapon` appears in three files,
  `IsTwoHanded` in two): damage, skill, attack, block values and the fist weapon's own animation state are untouched
  fields; with a shield the animation state comes from the shield, exactly as vanilla bare-fists-plus-shield already does;
  `GetCurrentBlocker` still blocks with the fists when no shield is held; `IsTwoHanded()` is used in exactly one place
  (auto-equip on pickup) and only against the *left* hand, which fist weapons never occupy; the in-hand claw visual is
  attached by hand slot and item hash, never by item type. Two visible side effects, both intended: the tooltip now says
  one-handed instead of two-handed, and a torch will now sit in your off-hand alongside fists. Nothing serialises the item
  type — a save/ZDO stores the prefab name and `m_shared` is resolved from the prefab on load — so there is no migration
  and no ServerSync compatibility concern.
- **CraftFromChests**: the stone oven pulls from nearby chests, meat racks still do not. `[Chests]
  PullForCookingStations` gated every prefab carrying a `CookingStation` component together — the meat racks
  (`piece_cookingstation`, `piece_cookingstation_iron`) *and* the oven (`piece_oven`) — so bread dough could not be baked
  through the storage wall without also feeding the group's raw meat to the racks. New `[Chests] PullForOvens=true` +
  `OvenPrefabs="piece_oven"` is a per-prefab exception: a cooking station pulls if `PullForCookingStations` is true **or**
  if `PullForOvens` is on and its prefab is listed. `PullForCookingStations` keeps its old "all of them" meaning. The
  cauldron (`piece_cauldron`) and CookingAdditions' `BCA_CookingPot` are `CraftingStation`s, not `CookingStation`s — they
  never reached this gate and already pull via `PullForCrafting=true`. `[Chests] SelfTest` now prints the gate that
  applies to every cooking-station prefab in the build, and confirms the cauldron/pot are crafting stations.

## 0.4.5 (2026-09-07)
- **FastMining/OreRegrowth**: fractured ore stages (`rock4_copper_frac` etc.) handled — mining is now fast after the first hit
  and regrowth records the fully-mined frac, respawning the original vein; **DualPowers**: Shift+interact sets slot 1, altar
  messages name the slot and key, HUD shows the slot-2 key, `nvlb.power clear/swap`.
- **CorpseRunPlus**: the grave compass is an off-screen waypoint — it sits on the grave while the grave is on screen and slides
  to the screen edge in its direction when it is not (`[CorpseRun] CompassMode=Edge`, `CompassEdgeMargin=60`; `Fixed` restores
  the old static arrow), and holding `[CorpseRun] ClearGraveKey` (default `Delete`) for `ClearGraveHoldSec` dismisses the grave
  marker and Grave Pull without the console.


## 0.4.4 (2026-09-07)
- TestCommands module (server-gated `[Debug] AllowTestCommands`, default off): `nvlb.give`, `nvlb.power`, `nvlb.tier`
  for testing on private servers.

## 0.4.3 (2026-09-07)
- Docs: rewritten Thunderstore page (features by theme, customization guide). No code changes.

## 0.4.2 (2026-09-07)
- **FoodNoDecay**: decay is now removed at the source. 0.4.1 let vanilla decay a food and then raised the value back afterwards,
  which pushed a rising max through `SetMaxHealth`/`SetMaxStamina`/`SetMaxEitr` every second and made the health/stamina bars
  pulse - lowering and refilling - once a second. The decay curve inside `Player.UpdateFood` is replaced instead, so the values
  never move and the bars are static. New `[Food] HidePulse` (default on) also stops the food icons and their countdowns
  flashing in the HUD; `[Food] PulseBelowSeconds` (default 0 = never) can keep the flash as a last-seconds warning.
- **ExtraSlots**: the bottom row is now two plain storage slots instead of the Z/X/C quick slots - `[Slots] GenericSlots = 2`,
  `[Slots] QuickSlots = 0`. Anything fits in them, they have no hotkey and no label. The quick-slot code path is untouched:
  setting `QuickSlots` above 0 brings the hotkey row back. Items saved by 0.4.1 in `quick1`..`quick3` are migrated into the
  matching generic slots on load, with a log line.
- **CorpseRunPlus**: grave features only activate after a death in this world; cleared on loot. The compass, Grave Pull and
  the scaled CorpseRun buff used to key off `PlayerProfile.HaveDeathPoint()`, which vanilla never clears - so an old character
  joining a new world arrived with the compass already pointing at a death spot from somewhere else. The grave is now recorded
  by the mod, stamped with the world name, and deleted when the grave is looted or by the new `nvlb.grave.clear` command.

## 0.4.1 (2026-09-07)
- **ExtraSlots**: proper side panel UI in the vanilla style, adapted from shudnal's ExtraSlots (public domain); main grid back
  to 4 rows. The extra slots now sit in their own wooden panel beside the inventory window — equipment in columns
  (Head/Chest/Legs, Back/Utility/Utility), a food column with fork icons, an ammo column with arrow icons, and the quick slots
  labelled with their own hotkeys underneath. Short horizontal labels instead of the vertical text of 0.4.0.
- New per-player settings `[Slots] PanelOffsetX`, `PanelOffsetY`, `PanelScale`. `[Slots] ShowUI = false` still turns the
  drawing off; the slots then fall back to plain extra rows under the bag and every item stays reachable.

## 0.4.0 (2026-09-07)
- **ExtraSlots** `[Slots]`: equipment slots (helmet/chest/legs/cape), 2 utility slots (Megingjord + Wishbone together), 3 food
  slots (auto-eat), 2 ammo slots, 3 quick slots (Z/X/C). Items are stored in the character's custom data, never in the vanilla
  grid, so a vanilla client cannot delete them; 3 rolling backups + `nvlb.slots.restore`; rescues items left behind by
  shudnal's ExtraSlots. Replaces ExtraSlots + ConditionalConfigSync + YamlDotNet.
- **Loadouts** `[Loadouts]`: Ctrl+V / Ctrl+B save the weapon+shield in hand; V / B equip both with one key.
- **CorpseRunPlus** `[CorpseRun]`: grave compass (HUD arrow + distance), respawn with Bread already eaten and 10 min Rested,
  Grave Pull (stamina regen up / drain down, strongest far from your grave, fading as you approach), and the vanilla Corpse Run
  buff scaled by distance from grave to home. Enemies are unchanged.
- Every number is server-synced and hot-reloads on the server; hotkeys and HUD offsets are per-player.


## 0.3.0 (2026-09-06)
- **Renamed** from `OrionQoL`. New plugin GUID `Nosferatu.NoVikingLeftBehind`, new config file
  `Nosferatu.NoVikingLeftBehind.cfg`, new console command `nvlb.status`, log source `NVLB`.
  Old `net.mjensen.orion.qol.cfg` settings are **not** migrated — re-apply them once.
- Ten new modules: `CombatRecharge`, `CraftFromChests`, `DualPowers`, `FastMining`,
  `FoodNoDecay`, `LongFires`, `PortalTrail`, plus the `ChestsSelfTest`, `EconomySelfTest` and
  `WorldSelfTest` developer helpers (off by default).
- Live config reload (hot reload): edits to the cfg file are picked up on a running server
  without a restart (`[General] HotReload`).

## 0.2.0 (2026-09-06)
- Foundation rebuilt on ServerSync (blaxxun-boop, MIT-0): version handshake, `EnforceClientMod`,
  synced vs. local config.
- `Frontier` / `Tiers`: world tier computed from boss keys, per-material tier map.
- Catch-up modules added: `TrailingTierDiscount`, `RichSmelting`, `TraderStock`, `OreRegrowth`,
  `VanguardShadow`, `PlaytimeRubberBand`, `GroupSkillCatchup`.
- `ServerKeys` (skill XP rate, skill loss on death, free build/craft, unlockable recipes) carried
  over from 0.1.0 unchanged in behavior.

## 0.1.0 (2026-09-06)
- Initial server-only release: `ServerKeys` module.
