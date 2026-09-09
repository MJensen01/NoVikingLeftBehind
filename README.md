# NoVikingLeftBehind — the ultimate Valheim quality-of-life mod

A BepInEx 5 mod for Valheim 1.0, built and maintained for a small dedicated-server group
and released here for anyone to use. MIT licensed.

All-in-one, server-enforced quality-of-life and catch-up mechanics: 37 modules covering inventory,
loadouts, combat (fists + shield), crafting from chests, building and gathering, food, corpse
runs, guardian powers, ore regrowth, portals and a frontier-based catch-up system. One DLL, installed on the server and by
every player. The server enforces every setting (via [ServerSync](https://github.com/blaxxun-boop/ServerSync)),
so nobody has to agree on config by hand.

0.9.0 is built and tested against Valheim `1.0.7` (network version 39) / BepInEx `5.4.2350`.
For servers held on the `default_pre1_0` branch (0.221.12) use 0.8.8.

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
   5.4.2350.
2. Start the server once to generate `Nosferatu.NoVikingLeftBehind.cfg` next to the other
   BepInEx configs, then edit the values you want (see below). Synced settings are pushed to
   every connecting client automatically.

## Config overview

37 modules, each with its own `[Section] Enabled` toggle, plus `Tiers` (shared config, always on,
no toggle of its own) — 38 rows in `docs/MODULES.md`, the full per-module table (side, section,
settings, defaults, hot-reload). Every gameplay number is server-synced and hot-reloads (edit the
cfg, it applies within a second — `Enabled` toggles need a restart to turn *on*, off is instant);
hotkeys and HUD offsets (`ExtraSlots`, `Loadouts`, `CorpseRunPlus`) are per-player, local settings.

### Catching up

World tier = highest boss killed; anything at or behind it is "behind the frontier" and gets
easier — the newest tier is never touched.

- **TrailingTierDiscount** `[Discount]` — recipes/pieces behind the frontier cost less
  (`CostMultiplier=0.5`, `ExtraPerTierBehind=0.0`, `MinAmount=1`).
- **RichSmelting** `[Smelting]` — smelter output and bar-recipe yield ×2 behind the frontier
  (`OutputMultiplier=2`, `RecipeYieldMultiplier=2`).
- **FastMining** `[Mining]` — behind-the-frontier ore mines faster, optionally ignores the
  tool-tier gate (`SpeedMultiplier=3.0`, `DropMultiplier=1.0`, `IgnoreToolTier=false`).
- **OreRegrowth** `[Regrowth]` — mined-out nodes behind the frontier respawn after a cooldown
  when nobody's nearby (`RegrowDays=7`, `MinPlayerDistance=64m`, `MaxPerTick=5`).
- **TraderStock** `[Trader]` — Haldor sells bars of behind-the-frontier metals, boss-key gated
  (`Items="Bronze:5:60,Iron:5:80,Silver:5:120,BlackMetal:5:150"`).
- **PortalTrail** `[Portals]` — normally non-teleportable materials behind the frontier go
  through portals (`AllowBehindFrontier=true`, `ExtraTiersBehind=0`).
- **PlaytimeRubberBand** `[Playtime]` — players below the group's median tracked hours get up
  to +100% gather/XP (`MaxBonus=1.0`, `MinGroupSize=3`, `WindowDays=14`).
- **GroupSkillCatchup** `[SkillCatchup]` — skills below the group's best level up faster, capped
  (`Bonus=1.0`, `MaxFactor=3.0`).
- **VanguardShadow** `[Vanguard]` — near a better-geared ally: less damage taken, more skill XP,
  faster stamina regen (`Radius=20`, `TierGap=1`, `DamageReduction=0.25`, `XpBonus=0.5`).

### Combat & powers

- **DualPowers** `[Powers]` — carry two (optionally three) Forsaken powers with independent
  cooldowns and a second hotkey (`Slots=2`, `IndependentCooldowns=true`, local `SecondSlotKey="G"`).
  At a boss altar plain E sets slot 1 (vanilla), Shift+E slot 2 and Ctrl+E slot 3, and the
  stone's tooltip says so (local `SecondSlotModifier="LeftShift"`, `ThirdSlotModifier="LeftControl"`);
  also draws every power icon as a circle with a thin cooldown/charge ring, generated at runtime
  (local `RoundIcons=true`, `CooldownRing=true`, `RingThickness=4`, `RingColor="E6C88AD9"`,
  `RingTrackColor="00000066"`).
- **CombatRecharge** `[Recharge]` — dealing/taking damage shaves seconds off power cooldowns,
  rate-capped (`SecondsPerHitDealt=2`, `SecondsPerHitTaken=3`, `MaxPerSecond=10`); each hit visibly
  jumps the charge ring forward.
- **FistsAndShields** `[Fists]` — fist weapons stop being two-handed, so Flesh Rippers or Hugo's
  Armory knuckles go in one hand and a shield in the other (`ExtraPrefabs=""`,
  `ExcludePrefabs=""`). Everything with the Unarmed skill is found automatically in `ObjectDB`.

### Inventory

- **ExtraSlots** `[Slots]` — dedicated equipment (helmet/chest/legs/cape), 2 utility, 3 food
  (auto-eat), 2 ammo and 2 plain generic slots outside the vanilla grid, so a vanilla client
  can't delete them (`QuickSlots` defaults to 0; raise it to swap the generic slots for a
  hotkeyed row instead). 3 rolling backups + `nvlb.slots.restore`; rescues items left behind by
  shudnal's ExtraSlots.
- **SafeSlots** `[SafeSlots]` — the safety net under ExtraSlots: your character's `.fch` is copied
  into `characters_local\nvlb-backups\` on its first login with the mod and again whenever items are
  rescued from an older extra-slots mod (local `CharacterBackup=true`, `KeepBackups=3`); one on-screen
  receipt says what moved; and every character save uploads the same extra-slot block to the server,
  which keeps the five newest per character under `config/nvlb/vault/<playerId>.json` (`VaultVersions=5`,
  `MinUploadIntervalSec=30`, blobs over 64 KB refused). Keyed on the profile's own permanent player id,
  so it is per character and survives a rename. Log in to empty slots with items in the vault and you are
  told so — never restored silently; `nvlb.slots.vault` / `nvlb.slots.vault restore [n]` do it, through
  the same injection path as `nvlb.slots.restore`. `SelfTest` proves the whole store headlessly.
- **Loadouts** `[Loadouts]` — Ctrl+Z / Ctrl+B save the weapon+shield in hand; Z / B equip both
  with one key (`Slots=2`). Or set `Loadout1Slots="1,2"` and the key equips whatever is in
  those hotbar slots at the moment you press it, with nothing stored. Keys default to Z/B
  since 0.8.1 (V is vanilla's auto-pickup) and are rebindable on Valheim's own Keyboard &
  Mouse settings page, as are the other NVLB hotkeys.
- **CraftFromChests** `[Chests]` — crafting, building, smelters, fires and the stone oven pull
  materials from nearby containers (`Range=20`, `PullForOvens=true`, `OvenPrefabs="piece_oven"`);
  meat racks never pull (`PullForCookingStations=false`) so saved meat stays saved (adapted from
  AzuCraftyBoxes, MIT-0).
- **RepairAll** `[Repair]` — opening a crafting station repairs every item in your inventory
  that station may repair, in one batch (`Trigger="OnOpen"`, or `Hotkey`/`Both` with local
  `Hotkey="R"`). Vanilla's own `InventoryGui.CanRepair` decides eligibility, so a forge still
  can't repair workbench gear and a too-low station level still refuses; one repair effect and
  one top-left message per batch (`ShowMessage=true`). ExtraSlots items included; building
  pieces are deliberately out of scope.

### Building

- **OverkillTools** `[Tools]` — a tool whose tier outclasses the target does more damage per swing:
  `1 + PerTierBonus x (toolTier - minToolTier)`, capped (`PerTierBonus=0.5`, `MaxMultiplier=3.0`,
  `AffectTrees/Rocks/Destructibles=true`). Gap 0 = vanilla, so day-one Meadows is untouched. Ore
  nodes stay FastMining's (`[Mining] OreNodes`), never both.
- **WorkbenchReach** `[Workbench]` — build range = vanilla + `PerTierMetres` x world tier +
  `PerLevelMetres` x (level-1), hard-capped (`PerTierMetres=2`, `PerLevelMetres=6`,
  `MaxRangeMetres=60`, `Stations="piece_workbench"`). Placement, deconstruct, repair, the build
  HUD and the visible circle all move together; the spawn-suppression area does not.
- **SettlementDiscount** `[Settlement]` — build pieces cost
  `max(1 - PerTierDiscount x worldTier, 1 - MaxDiscount)` (`0.10`/`0.50`, `MinAmount=1`), composed
  with the tier discount. Build pieces only; deconstruct refunds are scaled by the same factor so
  a refund never exceeds the price.
- **BuildersLoad** `[Load]` — within a bench's build range, listed building materials weigh
  `WeightMultiplier` (`0.5`), with a 3 s hysteresis on the way out (`Stations="piece_workbench,piece_stonecutter"`).
  Local player's own inventory only; the carry-weight cap is never touched.

### Survival & world

- **FoodNoDecay** `[Food]` — food holds its full value until it expires instead of decaying
  linearly (`KeepFraction=1.0`, `HidePulse=true`).
- **LongFires** `[Fires]` — fireplace fuel and hand torches last much longer
  (`FuelDurationMultiplier=5`, `HandTorchDurabilityMultiplier=5`).
- **CorpseRunPlus** `[CorpseRun]` — grave compass, respawn fed (Bread) + Rested
  (`RestedMinutes=10`), Grave Pull stamina boost that fades as you near your grave
  (`PullMinDistance=50m`, `PullFullDistance=1000m`), and the vanilla Corpse Run buff scaled by
  distance grave-to-home (`ScaledDurationPer100m=0.2`).

### On the water

- **SeaLegs** `[SeaLegs]` — with a crew aboard, your longship points closer into the wind: the
  dead zone narrows from vanilla's 36.9° to 32° / 27° / 23° with 2 / 3 / 4 aboard, so you can tack
  where a lone sailor has to row (`Crew2Cone=32`, `Crew3Cone=27`, `Crew4Cone=23`,
  `MaxCrewCounted=4`). **Cone only** — top speed, downwind force and the sail force factor are
  untouched, and the dead zone never closes below 20°. Crew count is written to the ship's ZDO by
  its owner, so every client agrees. Passengers also get the read-only wind arrow on their HUD
  (local `PassengerWindGauge=true`), with no rudder or sail controls.
- **Lookout** `[Lookout]` — stand off the tiller on a moving ship and the map fog reveals wider
  for you (`RadiusMultiplier=1.75`, `RequireMoving=true`, `MinSpeed=1.0`). Fog radius only: no
  pins, no messages, no shared state; the helmsman gets the vanilla radius.

### Server-side knobs that need no client mod

- **ServerKeys** `[ServerKeys]` — skill XP rate, skill loss on death, free build/craft keys
  pushed to vanilla clients (`SkillGainRate=1.0`, `SkillReductionRate=1.0`, `NoBuildCost=false`,
  `NoCraftCost=false`).
- **EnforceClientMod** `[General]` — require every connecting client to run a matching version.
- **HotReload** `[General]` — cfg edits on a running server are picked up live, no restart.
- **Access** `[Access]` — who may change settings from the in-game menu (`TweakAccess=Everyone`),
  how changes are announced (`Announce=Chat`) and the per-player rate limit
  (`MaxChangesPer10s=10`). See below.

### In-game settings menu

`SettingsMenu` `[SettingsMenu]` adds a **NoVikingLeftBehind** tab to Valheim's own Settings screen —
from the pause menu in game and from the main menu — listing all 235 settings: modules by theme down
the left with their `Enabled` toggles, the selected module's rows down the right with a one-line
hint under each name, the full description on hover, a reset-to-default button, and a search box
across name, key, hint and description.

Nothing is written by the client. A row asks the server over `ZRoutedRpc`; the server checks
`[Access] TweakAccess` and the setting's own tier against `adminlist.txt`, validates the value
against its declared range or choices, rate-limits the caller, then writes its own cfg file with a
timestamped backup — so the existing watcher and ServerSync push it to everyone through the path
that already existed, and `cfg.py`, hand edits and the backups keep working unchanged. Every change
is announced in chat and logged; the footer has Undo and Reset-module. Per-player settings are
written to your own cfg and never leave your machine. Rows you may not change are greyed with the
reason rather than hidden. The tab is built at runtime from clones of Valheim's own controls — the
mod ships no UI assets.

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

In-game (console, client-side): `nvlb.status` prints the live config; `nvlb.catalog [text]` lists
every setting with its hint, type, range, permission tier and whether it applies live (a dedicated
server writes the same table to `nvlb-catalog.tsv` beside its cfg at boot); `nvlb.slots.restore`
rolls `ExtraSlots`' storage back to one of its 3 automatic backups.

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

Currently `0.4.3`. See [`thunderstore/CHANGELOG.md`](thunderstore/CHANGELOG.md) for the full
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
