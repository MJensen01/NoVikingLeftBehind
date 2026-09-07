# Changelog — NoVikingLeftBehind

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
