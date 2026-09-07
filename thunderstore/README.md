# NoVikingLeftBehind

One mod, server-enforced settings. Install on the **server** (`BepInEx/plugins/NoVikingLeftBehind/`) and
on **every client** (r2modman: Settings > Import local mod, or search "NoVikingLeftBehind" once it's
listed here). Clients without a matching version are rejected while the server has
`EnforceClientMod = true`.

Every number lives in the server's `Nosferatu.NoVikingLeftBehind.cfg`; clients receive it on connect
via [ServerSync](https://github.com/blaxxun-boop/ServerSync) (MIT-0, blaxxun-boop).

## Modules (each has its own `Enabled` toggle)

- **Frontier** `[Frontier]` (both) — world tier = highest boss defeated; per-material tier map in `[Tiers]`; "behind the frontier" = `tier <= WorldTier - TiersBehind`.
- **ServerKeys** `[ServerKeys]` (server) — skill XP rate, skill loss on death, free-build/craft keys pushed to vanilla clients.
- **TrailingTierDiscount** `[Discount]` (client) — recipes/pieces behind the frontier cost less (`CostMultiplier`, default 0.5).
- **RichSmelting** `[Smelting]` (client, smelter owner) — smelter output and bar recipes ×2 behind the frontier.
- **TraderStock** `[Trader]` (client) — Haldor sells bars of behind-the-frontier metals, gated by the relevant boss key.
- **OreRegrowth** `[Regrowth]` (server) — mined-out ore nodes behind the frontier respawn after a cooldown (default 7 in-game days) when nobody is nearby.
- **VanguardShadow** `[Vanguard]` (client) — near a better-geared ally: -25% damage taken, +50% skill XP, +20% stamina regen.
- **PlaytimeRubberBand** `[Playtime]` (both) — players below the group's median tracked hours get up to +100% gather/XP.
- **GroupSkillCatchup** `[SkillCatchup]` (both) — skills below the group's best level up faster, capped at ×3.
- **CombatRecharge** `[Recharge]` (client) — dealing and taking damage shaves seconds off your forsaken power cooldowns.
- **DualPowers** `[Powers]` (client) — carry two forsaken powers with independent cooldowns and a second HUD icon.
- **FoodNoDecay** `[Food]` (client) — food holds its full value far longer instead of decaying linearly.
- **FastMining** `[Mining]` (client) — ore behind the frontier mines faster and can ignore the tool-tier gate.
- **LongFires** `[Fires]` (client) — fireplace fuel and hand torches last much longer.
- **PortalTrail** `[Portals]` (client) — metals and other normally non-teleportable items behind the frontier go through portals.
- **CraftFromChests** `[Chests]` (client) — crafting, building, smelters and fires pull materials from nearby containers (code adapted from AzuCraftyBoxes, MIT-0).
- **Status** `[Status]` (client) — the `nvlb.status` console command: world tier, catch-up settings and per-module state.
- **ChestsSelfTest** `[ChestsSelfTest]`, **EconomySelfTest** `[Economy]`, **WorldSelfTest** `[World]` (both) — developer self-test helpers, off by default.

Console (client): `nvlb.status` prints the live config.

## Dependencies

- [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) 5.4.2333

## Source & issues

https://github.com/MJensen01/NoVikingLeftBehind — MIT licensed.
