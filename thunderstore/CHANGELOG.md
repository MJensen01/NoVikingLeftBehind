# Changelog — NoVikingLeftBehind

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
