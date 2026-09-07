# NoVikingLeftBehind

For co-op groups where nobody can play every night. If half your crew is deep in iron and the
other half only gets online on weekends, this mod stops the gap from turning into a wall — no
more "the bronze crunch": ore mined out, the forge moved on, and the latecomer stuck ranging the
coast for scraps of copper everyone else abandoned weeks ago. One install replaces six other mods
and adds two dozen quality-of-life features on top. Every number below is a server setting, and
every one of them can be changed **while the server is running** — save the cfg file and it
applies within a second, no restart, no client re-download.

## How it thinks

The mod tracks the world's **frontier**: the highest boss your group has actually killed. Anything
at or behind that frontier is "old news" and gets easier; anything at or ahead of it — the boss
you haven't beaten yet — is untouched. Nobody is singled out as "behind"; the rule keys off the
*world's* progress, not any one player's.

Concretely: your group kills the Elder. World tier ticks up to 2. Bronze (tier 1) is now behind
the frontier, so all at once: bronze recipes and pieces cost half as much, smelters spit out
double the bars per ore, portals stop refusing to carry it, mined-out copper and tin nodes grow
back over time, and Haldor starts selling ingots of it. Iron — the new frontier — isn't touched at
all. The mod never makes the leaders' game easier; it only softens the trail behind them.

## Features by theme

### Catching up

- **TrailingTierDiscount** — that iron cuirass costs half the ore it used to, the moment iron
  stops being your group's edge. `Tune it:` `[Discount] CostMultiplier=0.5`, `ExtraPerTierBehind=0.0`, `MinAmount=1`
- **RichSmelting** — drop copper in a behind-the-frontier smelter and watch it spit out twice the
  bars per ore; craft a bronze recipe and get double the yield per craft. `Tune it:` `[Smelting] OutputMultiplier=2`, `RecipeYieldMultiplier=2`
- **FastMining** — a behind-the-frontier vein melts under your pickaxe instead of eating your
  stamina bar. `Tune it:` `[Mining] SpeedMultiplier=3.0`, `DropMultiplier=1.0`, `IgnoreToolTier=false`, `OreNodes="rock4_copper:1,rock4_copper_frac:1,MineRock_Tin:1,silvervein:3,silvervein_frac:3,MineRock_Obsidian:3,MineRock_Meteorite:4"`
- **OreRegrowth** — that copper vein your group stripped bare a month ago is standing again next
  time someone new needs it, quietly regrown while nobody was near — and only once the whole vein
  is gone, never while a half-mined chunk of it is still standing. `Tune it:` `[Regrowth] RegrowDays=7`, `CheckIntervalSec=60`, `MinPlayerDistance=64m`, `MaxPerTick=5`, `Prefabs="rock4_copper_frac:1:rock4_copper,silvervein_frac:3:silvervein,MineRock_Tin:1,MineRock_Obsidian:3,MineRock_Meteorite:4"`
- **TraderStock** — Haldor has bronze and iron bars on his shelf now, not just the trinkets he's
  always sold, and only once your group has actually earned the right (boss-key gated). `Tune it:` `[Trader] Items="Bronze:5:60,Iron:5:80,Silver:5:120,BlackMetal:5:150"`, `TraderNames="Haldor"`
- **PortalTrail** — that stack of iron you'd normally have to lug home on foot? Toss it through
  the portal like everything else — the newest tier still can't ride along, but everything behind
  it can. `Tune it:` `[Portals] AllowBehindFrontier=true`, `ExtraTiersBehind=0`, `AlwaysAllow=""`, `NeverAllow=""`
- **PlaytimeRubberBand** — the friend who only logs in Sundays gathers and levels noticeably
  faster than the group's daily grinders, without anyone ever slowing down. `Tune it:` `[Playtime] MaxBonus=1.0`, `MinGroupSize=3`, `WindowDays=14`, `GatherBonusEnabled=true`, `XpBonusEnabled=true`
- **GroupSkillCatchup** — your one-hand sword skill climbs noticeably faster while it trails the
  group's best swordsman — and stops mattering once you catch up. `Tune it:` `[SkillCatchup] Bonus=1.0`, `MaxFactor=3.0`, `WindowDays=14`
- **VanguardShadow** — stand near your better-geared friend and feel it: hits land softer, XP
  ticks up faster, stamina comes back quicker — the moment you drift out of range, it's gone. `Tune it:` `[Vanguard] Radius=20`, `TierGap=1`, `DamageReduction=0.25`, `XpBonus=0.5`, `StaminaRegen=0.2`, `RequireBehindFrontier=false`

### Combat & powers

- **DualPowers** — carry two Forsaken powers at once, each on its own cooldown, and hit **G** to
  fire the other one mid-fight instead of waiting out the first (the key is written on the second
  power icon, so you never have to guess). At a boss altar, interacting normally fills the first
  empty slot — hold **Shift** while interacting to put the power in slot 1 (**F**) instead, and
  the game tells you which slot and which key it landed on. `nvlb.power clear 1|2` and
  `nvlb.power swap` fix a power in the wrong slot. `Tune it:` `[Powers] Slots=2`, `IndependentCooldowns=true`, `CooldownMultiplier=1.0`, local `SecondSlotKey="G"`, local `Slot1Modifier="LeftShift"`
- **CombatRecharge** — every hit you land or take visibly chips seconds off your power's
  cooldown bar, so a hard fight brings your power back around, not the clock. `Tune it:` `[Recharge] SecondsPerHitDealt=2`, `SecondsPerHitTaken=3`, `MaxPerSecond=10`, `AffectAllSlots=true`
- **FistsAndShields** — put the Flesh Rippers (or Hugo's bronze knuckles) in one hand and a
  shield in the other. Vanilla calls every fist weapon two-handed, so equipping one drops your
  shield — even though bare fists and a shield have always worked fine together. Now they don't
  fight each other, and a torch will sit in your off-hand too. `Tune it:` `[Fists] ExtraPrefabs=""`, `ExcludePrefabs=""`

### Inventory

- **ExtraSlots** — dedicated slots for helmet/chest/legs/cape, two utility slots (Megingjord +
  Wishbone together), three auto-eating food slots, two ammo slots, and two generic slots for
  whatever doesn't fit anywhere else — all extra real estate outside your main bag, so it never
  eats into carry space. Stored outside the vanilla save package (a vanilla client or tool can't
  delete them), with 3 rolling backups and a rescue path for characters migrating off shudnal's
  ExtraSlots. `Tune it:` `[Slots] EquipmentSlots=true`, `UtilitySlots=2`, `FoodSlots=3`, `AmmoSlots=2`, `GenericSlots=2`, `QuickSlots=0`, `AutoEatFromFoodSlots=true`
- **Loadouts** — hold Ctrl and tap V to save your current weapon+shield, then just tap V later to
  snap both back into your hands mid-fight without opening the inventory. `Tune it:` `[Loadouts] Slots=2`, local `Loadout1Key="V"`, `Loadout2Key="B"`, `SaveModifier="LeftControl"`
- **CraftFromChests** — stand at the forge near your storage wall and craft straight through it —
  no more running back and forth for one more bar. The stone oven bakes straight out of the chests
  too, while meat racks never pull, so the raw meat you stashed for a recipe is still there when
  you go looking for it. `Tune it:` `[Chests] Range=20`, `PullForCrafting=true`, `PullForBuilding=true`, `PullForSmelters=true`, `PullForFires=true`, `PullForCookingStations=false`, `PullForOvens=true`, `OvenPrefabs="piece_oven"`, local `ToggleKey="LeftAlt+O"`

### Survival & world

- **FoodNoDecay** — that meal you ate ten minutes ago is still giving you full health and
  stamina right up until it actually runs out, instead of quietly fading the whole time. `Tune it:` `[Food] KeepFraction=1.0`, `HidePulse=true`, `PulseBelowSeconds=0`
- **LongFires** — load the hearth once and it's still burning hours later; your hand torch
  barely loses any charge on a long cave crawl. `Tune it:` `[Fires] FuelDurationMultiplier=5`, `HandTorchDurabilityMultiplier=5`, `InfiniteFuel=false`
- **CorpseRunPlus** — five things that make dying suck less: a HUD compass points straight at
  your grave so you're never guessing; you respawn with a meal already eaten and Rested already
  running; **Grave Pull** boosts your stamina regen the farther you are from your loot, easing off
  as you close in; and vanilla's own Corpse Run buff scales up the farther your grave was from
  home, so a death way out in the mountains actually helps on the long walk back. `Tune it:` `[CorpseRun] CompassEnabled=true`, `RespawnRestedEnabled=true (RestedMinutes=10)`, `RespawnFoodEnabled=true (RespawnFoods="Bread")`, `PullEnabled=true (PullMinDistance=50m, PullFullDistance=1000m)`, `ScaledEnabled=true (ScaledDurationPer100m=0.2)`
  The grave marker is an off-screen waypoint: it sits on your corpse while the corpse is on
  screen and slides to the edge of the screen in its direction when it is not, so it only
  points dead ahead when you are actually walking at it. Hold **Delete** for a second and a
  half to dismiss a grave you have given up on, no console needed.

### Server-side knobs that need no client mod

- **ServerKeys** — the admin-only dials every server owner reaches for on day one: skill XP rate,
  how much skill you lose on death, free build/craft, unlockable recipes without a workbench —
  applied to the world itself, so even a vanilla client feels the effect. `Tune it:` `[ServerKeys] SkillGainRate=1.0`, `SkillReductionRate=1.0`, `NoBuildCost=false`, `NoCraftCost=false`, `AllRecipesUnlocked=false`, `DeathKeepEquip=false`

## Customize everything

Every setting above lives in one file on the server: `Nosferatu.NoVikingLeftBehind.cfg`, generated
the first time the server boots with the mod installed. Edit it in a text editor, save, and the
change is live within a second — no restart, no re-download for players, because ServerSync pushes
the new values to every connected client automatically. The one exception: flipping a module's
`Enabled` line from **off to on** needs a server restart to install that module's patches (turning
one **off** is instant, same as everything else).

A handful of settings are deliberately local, not server-synced — hotkeys and HUD panel offsets
for `ExtraSlots`, `Loadouts` and `CorpseRunPlus` — because those are about *your* keyboard and
*your* screen, not the group's rules.

A real excerpt of the cfg:

```ini
[Discount]
CostMultiplier = 0.5

[Regrowth]
RegrowDays = 7

[Slots]
FoodSlots = 3

[CorpseRun]
RestedMinutes = 10
```

Two console commands round it out: `nvlb.status` (client) prints the live world tier and every
module's current settings, and `nvlb.slots.restore` rolls your `ExtraSlots` storage back to one of
its automatic backups if something ever looks wrong.

On a private test server the admin can add `[Debug] AllowTestCommands = true` to unlock three
helper commands for connected players - `nvlb.give`, `nvlb.power` and `nvlb.tier` - which a dedicated
server's console otherwise refuses; it is off by default, so nobody can hand themselves items.

## Install

**Players** — install via r2modman or the in-game Thunderstore mod manager (Online tab): search
**NoVikingLeftBehind** under the Valheim community and install it into your profile. First,
remove any of the mods it replaces from that profile: `SkillGainModifier`, `SmartSkills`,
`AzuCraftyBoxes`, `ExtraSlots`, `ConditionalConfigSync`, `YamlDotNet`. Join the server — if
`EnforceClientMod` is on, r2modman keeps you version-matched automatically.

**Server admins** — unzip the whole Thunderstore package contents into
`BepInEx/plugins/NoVikingLeftBehind/` on the dedicated server (depends on
[BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
5.4.2333). Start the server once to generate the cfg, then edit it to taste.
`[General] EnforceClientMod` (default on) rejects any connecting client that isn't running a
matching version — turn it off to let vanilla clients join alongside modded ones (they just won't
see the client-side features).

**FAQ**

- *Can a vanilla client join?* Not while `EnforceClientMod` is on — the server rejects it with an
  explanatory message. Turn that setting off if you want to allow vanilla clients in.
- *Will my items in the extra inventory slots survive a crash or a bad update?* Yes — they live
  outside the vanilla save package entirely, with 3 rolling automatic backups and
  `nvlb.slots.restore` to roll back if anything looks off.
- *Does it work alongside content mods, like a Jotunn item/creature pack?* Yes — NoVikingLeftBehind
  never touches prefabs, items, or creatures; it patches vanilla behavior (costs, yields, mining
  speed, cooldowns, and so on) and stays out of content mods' way.

## Credits & license

MIT licensed. [ServerSync](https://github.com/blaxxun-boop/ServerSync) by **blaxxun-boop**
(MIT-0), vendored as source. `CraftFromChests` adapts container-pulling logic from
[AzuCraftyBoxes](https://github.com/AzumattDev/AzuCraftyBoxes) by **Azumatt** (MIT-0).
`ExtraSlots`' panel UI is adapted from [shudnal/ExtraSlots](https://github.com/shudnal/ExtraSlots)
(Unlicense / public domain). Full details in `THIRD_PARTY.md`.

Source & issues: https://github.com/MJensen01/NoVikingLeftBehind

Sibling mod, server-tuning focused: [SmoothServer](https://github.com/MJensen01/SmoothServer).
