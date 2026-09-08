# Changelog — NoVikingLeftBehind

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
