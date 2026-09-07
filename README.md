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

1. Drop `NoVikingLeftBehind.dll` into `BepInEx/plugins/NoVikingLeftBehind/` on the dedicated
   server. Depends on [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
   5.4.2333.
2. Start the server once to generate `BepInEx/config/Nosferatu.NoVikingLeftBehind.cfg`, then
   edit the values you want (see below). Synced settings are pushed to every connecting client
   automatically.

## Config overview

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
- `EnforceClientMod` — require every connecting client to run a matching version.
- `HotReload` — cfg edits on a running server are picked up live, no restart.

In-game: `nvlb.status` (console command, client-side) prints the live config.

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

Currently `0.3.0` (renamed from `OrionQoL`; adds the Food, Powers, Mining, Fires, Portals and
Chests modules plus live config reload).

Name and Thunderstore namespace are **final**: package namespace/team `Nosferatu`, package
`NoVikingLeftBehind`. Both are immutable once the first version is uploaded — see
[`SmoothServer`](https://github.com/MJensen01/SmoothServer), its companion server-tuning mod,
and `PUBLISHING.md` in `MJensen01`'s local workspace for the upload steps.

**Upgrading from OrionQoL:** the plugin GUID and config file name changed, so the server
generates a fresh `Nosferatu.NoVikingLeftBehind.cfg` with defaults on first boot — copy your
old values across, delete the old `net.mjensen.orion.*.cfg` file and the old plugin folder, and
note that the catch-up data directory moved from `BepInEx/config/orion/` to
`BepInEx/config/nvlb/`.

## History

Split from the combined [`valheim-mods`](https://github.com/MJensen01/valheim-mods) repo
(archived) on 2026-09-07, at the point both mods reached their first Thunderstore release
(NoVikingLeftBehind 0.3.0). Full history up to that point lives in the archived repo.
