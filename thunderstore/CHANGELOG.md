# Changelog — NoVikingLeftBehind

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
