# NoVikingLeftBehind

A BepInEx 5 mod for Valheim 0.221.12, built and maintained for a small dedicated-server group
and released here for anyone to use. MIT licensed.

Server-enforced quality-of-life and catch-up mechanics. One DLL, installed on the server and by
every player. The server enforces every setting (via [ServerSync](https://github.com/blaxxun-boop/ServerSync)),
so nobody has to agree on config by hand.

Pre-release (see version plan below), built and tested against Valheim `0.221.12` / BepInEx
`5.4.2333`. A 1.0-compatible build will follow once the game updates.

## Install (players, via r2modman / Thunderstore)

1. Install [r2modman](https://thunderstore.io/package/ebkr/r2modman/) or the in-game Thunderstore
   mod manager.
2. Search **NoVikingLeftBehind** under the Valheim community and install it into your profile.
3. Join the server. If `EnforceClientMod` is on, the server rejects clients that don't have a
   matching version — r2modman keeps you updated automatically.

## Install (server owners)

1. Drop `NoVikingLeftBehind.dll` into `/config/bepinex/plugins/NoVikingLeftBehind/` on the
   dedicated server (the path BepInEx's plugin folder is mounted at on this project's own
   Docker host; a bare-metal BepInEx install uses `BepInEx/plugins/NoVikingLeftBehind/`
   instead). Depends on [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
   5.4.2333.
2. Start the server once to generate `Nosferatu.NoVikingLeftBehind.cfg` next to the other
   BepInEx configs, then edit the values you want (see below). Synced settings are pushed to
   every connecting client automatically.

## Config overview

24 modules, each with its own `[Section] Enabled` toggle, plus `Tiers` (shared config, always on,
no toggle of its own) — 25 rows in `docs/MODULES.md`, the full per-module table (side, section,
settings, defaults, hot-reload). Highlights:

Server-authoritative, synced to clients:
- `ServerKeys` — skill XP rate, skill loss on death, free build/craft, unlockable recipes.
- `Frontier` / `Tiers` — world tier from boss keys, per-material tier map; drives every
  "behind the frontier" catch-up module below.
- `TrailingTierDiscount`, `RichSmelting`, `TraderStock` — cheaper/faster progression for
  materials behind the group's frontier.
- `OreRegrowth` — mined-out nodes behind the frontier respawn after a cooldown.
- `VanguardShadow` — a damage/XP/stamina buff for under-geared players near a stronger ally.
- `PlaytimeRubberBand`, `GroupSkillCatchup` — catch-up bonuses scaled off the group's own
  hours/skill levels, not fixed numbers.
- `CombatRecharge`, `DualPowers` — forsaken powers: cooldown shaved by combat, and two powers
  carried at once with independent cooldowns.
- `FoodNoDecay`, `LongFires` — food keeps its value far longer; fireplace fuel and hand torches
  burn much longer.
- `FastMining`, `PortalTrail` — behind-the-frontier ore mines faster, and normally
  non-teleportable materials behind the frontier go through portals.
- `CraftFromChests` — crafting, building, smelters and fires pull materials from nearby
  containers (adapted from AzuCraftyBoxes, MIT-0 — see `THIRD_PARTY.md`).
- `ExtraSlots` — dedicated equipment (helmet/chest/legs/cape), 2 utility, 3 food, 2 ammo and
  2 plain generic slots outside the vanilla grid, so a vanilla client can't delete them
  (`QuickSlots` defaults to 0; raise it to bring back a hotkeyed row instead of the generic
  slots). 3 rolling backups + `nvlb.slots.restore`.
- `Loadouts` — Ctrl+V / Ctrl+B save the weapon+shield in hand; V / B equip both with one key.
- `CorpseRunPlus` — grave compass, respawn fed + Rested, Grave Pull stamina boost, and the
  vanilla Corpse Run buff scaled by distance from grave to home.
- `EnforceClientMod` — require every connecting client to run a matching version.
- `HotReload` — cfg edits on a running server are picked up live, no restart.

Every gameplay number above is server-synced and hot-reloads; hotkeys and HUD offsets
(`ExtraSlots`, `CorpseRunPlus`) are per-player, local settings.

## Mods this replaces

Uninstall these before installing NoVikingLeftBehind — same ground, one DLL instead of six:
[SkillGainModifier](https://valheim.thunderstore.io/package/JuJuz1/SkillGainModifier/) (`ServerKeys`),
[SmartSkills](https://valheim.thunderstore.io/package/Smoothbrain/SmartSkills/) (`ServerKeys`/`GroupSkillCatchup`),
[AzuCraftyBoxes](https://valheim.thunderstore.io/package/Azumatt/AzuCraftyBoxes/) (`CraftFromChests`),
[ExtraSlots](https://valheim.thunderstore.io/package/shudnal/ExtraSlots/),
[ConditionalConfigSync](https://valheim.thunderstore.io/package/shudnal/ConditionalConfigSync/) and
[YamlDotNet](https://valheim.thunderstore.io/package/ValheimModding/YamlDotNet/) (both only needed
by ExtraSlots) — all replaced by `ExtraSlots`. `SlotsRescue` migrates items shudnal's ExtraSlots
left behind.

In-game (console, client-side): `nvlb.status` prints the live config; `nvlb.slots.restore` rolls
`ExtraSlots`' storage back to one of its 3 automatic backups.

## Credits

- [ServerSync](https://github.com/blaxxun-boop/ServerSync) by **blaxxun-boop**, MIT-0 — vendored
  as source in `src/Vendor/ServerSync.cs` (unmodified; see the file header before editing).
- See [`THIRD_PARTY.md`](THIRD_PARTY.md) for the full list and license texts.

## Building from source

See `src/Directory.Build.props` for how game/BepInEx references resolve — either via the
build-lab's container mount, or via `VALHEIM_MANAGED` / `VALHEIM_BEPINEX_CORE` environment
variables (or `-p:ValheimManagedDir=... -p:BepInExCoreDir=...`) pointing at a local Valheim +
BepInEx install. CI (`.github/workflows/build.yml`) fetches both from scratch on every push.

```powershell
$env:VALHEIM_MANAGED = "D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed"
$env:VALHEIM_BEPINEX_CORE = "D:\SteamLibrary\steamapps\common\Valheim\BepInEx\core"
dotnet build src -c Release
python scripts\package.py   # builds thunderstore/dist zip
```

## Versioning

Currently `0.4.2`. See [`thunderstore/CHANGELOG.md`](thunderstore/CHANGELOG.md) for the full
version history, including the 0.3.0 rename from the project's original name (`OrionQoL`) and
its one-time config-migration notes.

Name and Thunderstore namespace are **final**: package namespace/team `Nosferatu`, package
`NoVikingLeftBehind`. Both are immutable once the first version is uploaded — see
[`SmoothServer`](https://github.com/MJensen01/SmoothServer), its companion server-tuning mod,
and `PUBLISHING.md` in `MJensen01`'s local workspace for the upload steps.

## History

Split from the combined [`valheim-mods`](https://github.com/MJensen01/valheim-mods) repo
(archived) on 2026-09-07, at the point both mods reached their first Thunderstore release
(NoVikingLeftBehind 0.3.0). Full history up to that point lives in the archived repo.
