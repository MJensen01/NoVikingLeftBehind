# NVLB settings inventory — complete, with first-pass Simple/Advanced classification

Repo: `C:\Users\mij99\Documents\AI\Valheim\NoVikingLeftBehind`, branch `release/0.10.3`.
Sources read: every `BindSynced(`/`BindLocal(` call under `src/` (extracted with a balanced-paren
parser, 265 non-comment call sites), `src/Config/Opt.cs` (the metadata system),
`src/Config/PickerSpec.cs`, `src/FeatureModule.cs` (the automatic `[Section] Enabled` bind),
`src/Access/SmoothServerBridge.cs` (the foreign SmoothServer allowlist) and `docs/MODULES.md`
(human descriptions + defaults).

---

## 0. How the metadata system works (needed to read the table)

`Opt` (`src/Config/Opt.cs`) is attached at bind time and is the only description of a setting the
UI ever sees. Factories:

| Factory | Means | UI control |
|---|---|---|
| `Opt.B(hint)` | boolean | checkbox |
| `Opt.N(hint, min, max, step)` | number, **advisory** range (never passed to BepInEx as `AcceptableValueRange`, on purpose — BepInEx clamps on load and would silently rewrite a server cfg). Enforced only by the tweak door. | slider / number box |
| `Opt.T(hint)` | free text the owning module parses (prefab lists, hex colours, key names) | text box |
| `Opt.C(hint, choices…)` | pick-one; enums fill their own choices | drop-down |

Modifiers: `.Admin()` → only `adminlist.txt` players may change it; `.Restart()` → `Live=false`,
row greyed, change needs a restart; `.As(label)` → override the CamelCase-derived label;
`.Pick(PickerSpec)` → the free-text list can be ticked off a list of what is actually in the world
(items / materials / pieces / stations / ore nodes / foods / traders / torches / cooking stations),
optionally with numeric fields per entry (`Tier`, `Multiplier`, `Stack`, `Price`, `Floor`).
`.Range(min,max,step)` bolts a range onto a non-`N` factory.

Every `FeatureModule` gets a `[Section] Enabled` bound for it automatically
(`FeatureModule.Configure`, line 106) with `Opt.B(module.Hint).As("Enabled")`. **Restart rule, the
same for all 41**: flipping `Enabled` **off** is live; flipping it **on** after the process booted
with it off does **nothing until a restart** (patches are installed once, at `Awake`). Eight
modules override `EnabledOpt` to `.Admin()`: `Access`, `ChestsSelfTest`, `Debug`, `Economy`,
`PickerSelfTest`, `SlotsSelfTest`, `World`, `ServerKeys`. No module overrides `DefaultEnabled`, so
**every module ships on**.

### Count reconciliation vs. the live catalog line `ConfigCatalog: 294 settings (221 synced / 73 local)`

| | count |
|---|---|
| Explicit `BindSynced`/`BindLocal` call sites in modules/plugin (excluding the 9 wrapper/abstract declarations in `FeatureModule.cs`, the 2 forwarders in `Plugin.cs`, and 4 XML-doc examples in `Opt.cs`) | 254 |
| minus `[Catchup] SelfTest`, **bound twice** — once by `GroupSkillCatchupModule.cs:112` and once by `PlaytimeRubberBandModule.cs:147`. BepInEx returns the same `ConfigEntry` for the same section+key, so the catalog holds **one** row. | −1 |
| = distinct explicit settings | **253** |
| plus one automatic `[Section] Enabled` per `FeatureModule` (41 classes, verified: `grep -c ": FeatureModule"` = 41, and `docs/MODULES.md` states "exactly 41") | +41 |
| **total** | **294** ✔ |

Synced/local split, same arithmetic: 74 explicit `BindLocal` call sites − 1 (the duplicated
`[Catchup] SelfTest`) = **73 local** ✔; 254 − 74 = 180 explicit synced + 41 `Enabled` toggles
(all synced) = **221 synced** ✔.

**There is no discrepancy** — the only thing that looks like one is the double-bound
`[Catchup] SelfTest`, which is deliberate (one shared diagnostic flag for the two Catchup modules).

Section count: 44 config sections. 41 have an `Enabled`; three do not — `[Catchup]` (only the
shared SelfTest flag), `[General]` (plugin-level, bound in `Plugin.cs`), `[Tiers]` (shared config,
bound from `Plugin.Awake` via `Tiers.BindConfig()`, deliberately not a `FeatureModule`).

---

## 1. Complete inventory — all 294 settings

Legend: **S** = server-synced, **L** = machine-local. **A** = `.Admin()`. **R** = `.Restart()`
(not live). Tiers: **E**ssential · **A**dvanced · **D**iagnostic · **M**odule-toggle.

### [Access] — the settings door (6)

| Key | S/L | Default | Meaning (player words) | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Let players change settings from the in-game menu at all | M | module toggle; off = tab lists but nothing can be changed |
| TweakAccess | S·A | Everyone | Who is allowed to change a server setting: everyone, or admins only | **E** | first thing an admin with 5k downloads wants to lock down |
| Announce | S·A | Chat | How a setting change is told to the server: chat / on-screen / both / off | **E** | Matt's group uses this daily; one picker |
| MaxChangesPer10s | S·A | 10 | How many changes one player may make in ten seconds | A | anti-abuse tuning |
| SelfTest | S·A | false | Server-start proof that the settings door works | D | debugging only |
| NetworkSelfTest | S·A | false | Server-start proof that the SmoothServer panel works | D | debugging only |

### [AmmoHud] — ammo readout (6)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Show your ammo count on the HUD | M | |
| OffsetX | L | 0 | Nudge the ammo readout sideways (px) | A | cosmetic placement |
| OffsetY | L | 0 | Nudge the ammo readout up/down (px) | A | cosmetic placement |
| Scale | L | 0.8 | Size of the ammo readout (1 = hotbar size) | A | cosmetic |
| Alpha | L | 0.65 | How solid the ammo readout looks | A | cosmetic |
| BackgroundAlpha | L | 0.45 | How solid the tile background behind it looks | A | cosmetic |

### [Builders] — BuildersGuild: cheap building at your base (22)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Cheap building at your base (the yard + the framing rule) | M | |
| YardRadius | S | 80 | How far from a station your building stays cheap (m) | A | radius knob |
| Materials | S | `Wood:0.5,RoundLog:0.5,FineWood:0.5,Stone:0.5,Iron:0.5,Bronze:0.5,Copper:0.5,BlackMetal:0.5` | How much cheaper each material is inside your yard | A | per-material list; the Simple view should drive this via a preset, not expose the list |
| Stations | S | `piece_workbench:Wood\|RoundLog\|FineWood,piece_stonecutter:Stone,forge:Iron\|Bronze\|Copper,blackforge:BlackMetal` | Which station makes which material cheap | A | per-station mapping |
| StationLevelScaling | S | false | Make the discount grow with the station's upgrade level | A | tuning |
| MinAmounts | S | "" | Lowest each material can be discounted to (`Iron:0` lets iron drop off entirely) | A | per-material floors; Matt runs a live override here |
| StructuralPieces | S | "" | Extra beams/poles to add to (or `-name` drop from) the framing rule | A | prefab list |
| StructuralFirstCost | S | 1 | What a free-standing beam costs, per material | A | tuning |
| StructuralAttachedCost | S | 0 | What a beam snapped onto another beam costs | A | tuning |
| StructuralInYardOnly | S | false | Only apply the cheap-beams rule inside a yard | A | tuning |
| RecordAllPieces | S | true | Refund exactly what a piece cost, wherever it was built | A | correctness switch, not a taste knob; leave on |
| HysteresisSeconds | S | 3 | How long the yard lingers after you walk out of it | A | anti-flicker timer |
| CheckIntervalSeconds | S | 0.5 | How often the yard check runs | A | perf timer |
| ShowTooltip | S | true | Explain the discount in the build menu | A | UI nicety |
| SkillEnabled | S | true | Building gets cheaper the more you have built (Builder skill) | **E** | a whole progression system; people want it on/off |
| SkillXpPerMaterial | S | 1 | How fast the Builder skill goes up | A | curve tuning |
| SkillMaxDiscount | S | 0.30 | Biggest discount the Builder skill can reach | A | compounds with 4 other knobs → preset |
| RhythmEnabled | S | true | Placing the same piece repeatedly gets cheaper | A | sub-feature toggle |
| RhythmWindowSec | S | 20 | How long a building streak survives a pause | A | timer |
| RhythmPerRepeat | S | 0.05 | Discount per repeat of the same piece | A | compounds → preset |
| RhythmMax | S | 0.25 | Biggest discount a building streak can reach | A | compounds → preset |
| SelfTest | L·A·R | false | Prove the yard/framing/skill maths in the log (35 checks) | D | |

### [Carry] — carry weight (4)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | More carry weight, and a stronger Megingjord | M | |
| BaseCarryWeight | S | 450 | How much you can carry before you are over-loaded (vanilla 300) | **E** | named in the brief; the single most-asked-for number |
| BeltBonus | S | 300 | How much extra the Megingjord belt gives (vanilla 150) | **E** | pairs with the above; one number |
| BeltItems | S | `BeltStrength` | Which items count as a carry belt | A | item picker, modded-content escape hatch |

### [Catchup] — shared diagnostic flag (1)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| SelfTest | L·A | false | Run a one-time catch-up test with fake data | D | **bound twice** (GroupSkillCatchup + PlaytimeRubberBand), one catalog row |

### [Chests] — craft from nearby containers (17)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Craft, build, smelt and cook using nearby chests | M *(show in Simple)* | the single biggest QoL switch in the mod — the one module toggle that belongs on the Simple page |
| Range | S | 20 | How far a chest can be and still count (m) | **E** | the one number everyone tunes |
| PullForCrafting | S | true | Let crafting and upgrading pull from chests | A | per-path toggle |
| PullForBuilding | S | true | Let building pull from chests | A | per-path toggle |
| PullForSmelters | S | true | Let smelters and kilns pull ore and fuel from chests | A | per-path toggle |
| PullForFires | S | true | Let fires and torches pull fuel from chests | A | per-path toggle |
| PullForCookingStations | S | false | Let every cooking station (incl. meat racks) pull ingredients | A | deliberately off; niche |
| PullForOvens | S | true | Let ovens alone pull ingredients and fuel | A | per-path toggle |
| OvenPrefabs | S | `piece_oven` | Which cooking stations count as an oven | A | prefab picker |
| LeaveOneItem | S | false | Always leave one item behind rather than emptying a chest | A | taste knob |
| IncludeVehicles | S | true | Count cart and ship cargo as nearby chests | A | taste knob |
| ExcludedContainers | S | `piece_chest_private` | Containers that are never pulled from | A | picker list |
| ExcludedItems | S | "" | Items that are never pulled out of a chest | A | picker list |
| ShowNearbyCount | S | true | Show have/needed counts that include nearby chests | A | UI nicety |
| Diagnostics | L | false | Log every craft-from-chests decision | D | debugging only |
| ToggleKey | L | `LeftAlt+O` | Key that turns chest-pulling off for you alone | A | keybind |
| SelfTest | L·A | false | One-time headless self-test at world load | D | bound by ChestsSelfTestModule |

### [ChestsSelfTest] (1)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Run the craft-from-chests diagnostic module | M | diagnostic-only module — hide its toggle too |

### [CorpseRun] — the run back to your body (36)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Help getting back to your corpse (compass, food, rest, stamina) | M | |
| CompassEnabled | S | true | Show a compass pointing at where you died | **E** | the headline feature of this module |
| CompassHideDistance | S | 10 | How close you get before the compass hides (m) | A | |
| CompassUpdateSec | S | 0.25 | Seconds between compass refreshes | A | perf timer |
| CompassOffsetX | L | 0 | Move the compass sideways on screen | A | cosmetic |
| CompassOffsetY | L | 200 | Move the compass up/down on screen | A | cosmetic |
| CompassArrow | L | `^` | Character used as the compass arrow | A | cosmetic |
| CompassArrowScale | L | 1.6 | Size of the compass arrow | A | cosmetic |
| CompassMode | L | Edge | Marker sticks to the screen edge, or sits in one place | A | cosmetic picker |
| CompassEdgeMargin | L | 60 | Pixel gap kept between the marker and the screen edge | A | cosmetic |
| HintAlpha | L | 0.55 | How faint the "hold to dismiss" hint is | A | cosmetic |
| HintScale | L | 0.8 | How small the "hold to dismiss" hint is | A | cosmetic |
| ClearGraveKey | L | `Delete` | Key held to dismiss the grave marker | A | keybind |
| ClearGraveHoldSec | L | 1.5 | How long to hold that key | A | |
| CycleGraveKey | L | `None` | Key that points the compass at your next grave | A | keybind |
| MinItemsToTrack | S | 1 | How much a grave must hold before it is tracked | A | |
| LootMatchDistance | S | 20 | How far a tombstone may be from your death point to count (m) | A | |
| RespawnFoodEnabled | S | true | Give free food when you respawn | **E** | very visible generosity switch |
| RespawnFoods | S | "" | Force one food list instead of picking by world progress | A | admin override; picker |
| RespawnFoodsByFrontier | S | 8-stage list (Meadows…DeepNorth) | Which food each stage of world progress grants | A | big derived table |
| RespawnFoodCount | S | 1 | How many foods to hand out on respawn | A | |
| RespawnRestedEnabled | S | true | Give the Rested buff when you respawn | A | sub-toggle of the same idea |
| RestedMinutes | S | 10 | Minutes of Rested granted on respawn | A | |
| PullEnabled | S | true | Stamina buff while your corpse is far away | A | sub-toggle |
| PullMinDistance | S | 50 | Distance from the grave before the stamina buff starts (m) | A | |
| PullFullDistance | S | 1000 | Extra distance beyond that for full strength (m) | A | |
| PullMaxRegenBonus | S | 1.0 | Extra stamina regeneration at full strength | A | |
| PullMaxDrainReduction | S | 0.5 | How much cheaper running/jumping gets at full strength | A | |
| PullUpdateSec | S | 1 | Seconds between buff-strength recalculations | A | perf timer |
| PullIconFrom | S | `Rested` | Which status effect to borrow the buff icon from | A | cosmetic/internal |
| ScaledEnabled | S | true | Stretch the vanilla loot buff by how far the grave was from home | A | sub-toggle |
| ScaledDurationPer100m | S | 0.2 | Extra loot-buff time per 100 m from home | A | |
| ScaledMaxDurationSec | S | 900 | Longest the loot buff can be stretched to | A | |
| ScaledExtraRegen | S | 0.5 | Extra stamina regen added at full distance | A | |
| ScaledRegenFullDistance | S | 1000 | Distance from home where that extra regen is full (m) | A | |
| SelfTest | L·A·R | false | Prove the corpse-run buffs with no players online | D | |

### [Debug] — TestCommands (2)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Register the `nvlb.` test console commands | M | diagnostic-only module |
| AllowTestCommands | S·A | false | Let connected players use the `nvlb` cheat commands | D | cheat/debug master switch |

### [Discount] — TrailingTierDiscount (5)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Old-tier recipes and build pieces cost less | M | **must be on at startup or Settlement + Builders do nothing** — flag this in the UI |
| CostMultiplier | S | 0.5 | What an old-tier recipe costs, as a fraction of normal | **E** | named in the brief; the primary discount strength |
| ExtraPerTierBehind | S | 0.0 | Extra discount for every further tier behind | A | compounds |
| MinAmount | S | 1 | Lowest a discounted requirement can drop to | A | floor |
| StationMultipliers | S | "" | Extra per-station discounts (for another mod's recipes) | A | picker list |

### [Economy] — EconomySelfTest (2)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Run the Economy diagnostic module | M | diagnostic-only module |
| SelfTest | L·A | false | Log what the Economy modules would do, at world load | D | |

### [Fires] — LongFires (5)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Fires and torches burn far slower | M | |
| FuelDurationMultiplier | S | 5 | How many times slower fires burn through fuel | **E** | one number, huge quality-of-life effect |
| InfiniteFuel | S | false | Fires never run out of fuel at all | A | the nuclear option; keep in Advanced |
| HandTorchDurabilityMultiplier | S | 5 | How many times slower hand torches wear out | A | secondary to the above |
| HandTorchItems | S | `Torch,TorchMist` | Which items count as a hand torch | A | picker list |

### [Fists] — FistsAndShields (3)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Wear fist weapons together with a shield | M | |
| ExtraPrefabs | S | "" | Extra two-handed weapons to make one-handed | A | picker list |
| ExcludePrefabs | S | "" | Fist weapons to leave two-handed | A | picker list |

### [Food] — FoodNoDecay (6)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Eaten food stays at full strength until it expires | M | |
| KeepFraction | S | 1.0 | How much of a food's benefit you keep as its timer runs down (1 = all of it) | **E** | the whole module in one number |
| CurveExponent | S | 0.3 | Shape of the fade below that floor | A | maths knob, meaningless at the default |
| HidePulse | S | true | Stop food icons flashing | A | cosmetic |
| PulseBelowSeconds | S | 0 | Still flash the icons this many seconds before food runs out | A | cosmetic |
| SelfTest | L·A·R | false | Log a decay-maths proof for one sample food | D | |

### [Frontier] — world progress tier (3)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Track how far the world has progressed (used by every catch-up feature) | M | |
| TierOverride | S | −1 | Force the world's progress tier instead of reading boss kills (−1 = auto) | D | explicitly a testing override |
| TiersBehind | S | 1 | How far behind the world's progress a material must be to count as "old" | A | the master gate for every catch-up feature — powerful but conceptual |

### [General] — plugin-level (3, no Enabled)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Mode | L·A·R | Auto | Which half of the mod this install runs (client/server/auto) | D | install plumbing; never touched in normal play |
| HotReload | L·A | true | Apply config edits without a restart | D | plumbing; turning it off breaks the settings tab's whole point |
| EnforceClientMod | S·A | true | Only players who have this mod may join | **E** | a real server-admin decision, first week |

### [Load] — BuildersLoad: materials weigh less near a bench (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Building materials weigh less near a workbench | M | |
| WeightMultiplier | S | 0.5 | How light building materials feel near a station | A | overlaps `[Carry] BaseCarryWeight` — expose one |
| Materials | S | 19 building materials | Which materials get the weight discount | A | picker list |
| Stations | S | `piece_workbench,piece_stonecutter` | Which stations count as nearby | A | picker list |
| HysteresisSeconds | S | 3 | How long the discount lingers after you leave | A | anti-flicker |
| CheckIntervalSeconds | S | 0.5 | How often the nearby-station check runs | A | perf timer |
| SelfTest | L·A·R | false | Log resolved material weights once per world load | D | |

### [Loadouts] — weapon loadouts (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Save and equip weapon/shield sets with a key | M | |
| Slots | S | 1 | How many weapon loadouts each player gets (0–4) | **E** | you must raise this to 2 before the second key does anything — a known confusion |
| Loadout1Key | L | `Z` | Key that equips loadout 1 | A | rebindable on Valheim's own keyboard page; cfg is only the default |
| Loadout2Key | L | `None` | Key that equips loadout 2 | A | same |
| SaveModifier | L | `LeftAlt` | Key held to save instead of equip | A | same |
| Loadout1Slots | L | "" | Hotbar slots loadout 1 equips (e.g. `1,2`); empty = use the saved pair | A | power-user mode |
| Loadout2Slots | L | "" | Same, for loadout 2 | A | power-user mode |

### [Lookout] — see further as a passenger (4)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | See further while riding a ship you are not steering | M | |
| RadiusMultiplier | S | 1.75 | How much more map a lookout reveals (1 = off) | A | single knob but a niche feature |
| RequireMoving | S | true | Only while the ship is actually moving | A | |
| MinSpeed | S | 1.0 | How fast the ship must go to count as under way (m/s) | A | |

### [Mining] — FastMining (6)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Old-tier ore mines faster and drops more | M | |
| SpeedMultiplier | S | 3.0 | How much faster old-tier ore nodes mine | **E** | one number, immediately felt |
| DropMultiplier | S | 1.0 | Extra ore per node | **E** | the "make copper less of a slog" knob; off by default so people look for it |
| IgnoreToolTier | S | false | Let any pickaxe mine old-tier ore | A | rule-bend |
| OreNodes | S | `rock4_copper:1,rock4_copper_frac:1,MineRock_Tin:1,silvervein:3,silvervein_frac:3,MineRock_Obsidian:3,MineRock_Meteorite:4` | Which ore nodes this applies to, and their tier | A | picker list; also excluded from OverkillTools, so editing it has a second effect |
| SelfTest | L·A·R | false | Log ore-node speed test results once per load | D | |

### [PickerSelfTest] (2)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Run the list-picker diagnostic module | M | diagnostic-only module |
| SelfTest | L·A | false | Prove every picker's parse/serialise round-trip at world load | D | |

### [Playtime] — PlaytimeRubberBand (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Extra gathering and XP for players who have played less | M | |
| RecomputeSec | S | 60 | How often the server recalculates the bonus | A | timer |
| WindowDays | S | 14 | How many recent days of playtime count | A | |
| MinGroupSize | S | 3 | Group size below which nobody gets the bonus | A | gate |
| MaxBonus | S | 1.0 | Biggest catch-up bonus (1.0 = up to double) | **E** | the strength dial for the mod's whole reason for existing |
| GatherBonusEnabled | S | true | Give the furthest-behind players extra item drops | A | sub-toggle |
| XpBonusEnabled | S | true | Give the furthest-behind players extra skill XP | A | sub-toggle; compounds with 3 other XP knobs |
| SelfTest | L·A | false | *(see `[Catchup] SelfTest` — bound here, listed once there)* | — | duplicate bind, not a separate row |

*(row count for this section is 7: Enabled + 6 explicit; the SelfTest line above is the second bind of `[Catchup] SelfTest`, counted in `[Catchup]`.)*

### [Portals] — PortalTrail (5)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Let old-tier ore go through portals | M | |
| AllowBehindFrontier | S | true | Let ore behind the frontier ride a portal | **E** | named in the brief; the classic "portals for ore" decision |
| ExtraTiersBehind | S | 0 | Extra margin before ore counts as old enough for portals | A | overlaps `[Frontier] TiersBehind` |
| AlwaysAllow | S | "" | Items always allowed through a portal | A | picker list |
| NeverAllow | S | "" | Items that may never ride a portal | A | picker list |

### [Powers] — DualPowers (18)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Carry more than one Forsaken power | M | |
| Slots | S | 2 | How many Forsaken powers you can carry (1–3) | **E** | the whole module in one number |
| IndependentCooldowns | S | true | Each power has its own cooldown | A | balance knob |
| CooldownMultiplier | S | 1.0 | Multiplier on every power's cooldown length | A | balance knob |
| SecondSlotKey | L | `G` | Key that fires the second power | A | keybind |
| ThirdSlotKey | L | `None` | Key that fires the third power | A | keybind |
| SecondSlotModifier | L | `LeftShift` | Key held at an altar to set the second power | A | keybind |
| ThirdSlotModifier | L | `LeftControl` | Key held at an altar to set the third power | A | keybind |
| Slot1Modifier | L | `LeftShift` | **Obsolete since 0.8.0 — bound and ignored** | D | hide entirely; it does nothing and reads like a real setting |
| ShowHud | L | true | Show a HUD icon for the extra power slot | A | cosmetic |
| HudOffsetX | L | 84 | Move the extra power icon sideways (px) | A | cosmetic |
| HudOffsetY | L | 0 | Move the extra power icon up/down (px) | A | cosmetic |
| RoundIcons | L | true | Draw power icons as circles | A | cosmetic |
| CooldownRing | L | true | Show a charging ring around each power icon | A | cosmetic |
| RingThickness | L | 4 | Thickness of that ring (px) | A | cosmetic |
| RingColor | L | `E6C88AD9` | Colour of the filled part of the ring (RGBA hex) | A | cosmetic |
| RingTrackColor | L | `00000066` | Colour of the empty part of the ring (RGBA hex) | A | cosmetic |
| SelfTest | L·A·R | false | Run power-slot self tests on a dedicated server | D | |

### [Recharge] — CombatRecharge (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Fighting shortens your Forsaken power cooldown | M | |
| SecondsPerHitDealt | S | 2 | Cooldown seconds removed per hit you land | A | balance |
| SecondsPerHitTaken | S | 3 | Cooldown seconds removed per hit you take | A | balance |
| MaxPerSecond | S | 5 | Most cooldown that can come off in one real second | A | safety cap |
| AffectAllSlots | S | true | Also shorten the extra power slots | A | |
| CountPlayerTargets | S | false | Count hits on other players | A | PvP edge case |
| ShowMessages | L | false | Show a message every time a hit shortens a cooldown | A | noisy; cosmetic |

### [Regrowth] — OreRegrowth (8)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Mined-out ore comes back after a while | M | |
| Prefabs | S | `rock4_copper_frac:1:rock4_copper,silvervein_frac:3:silvervein,MineRock_Tin:1,MineRock_Obsidian:3,MineRock_Meteorite:4` | Which ore comes back, and what it comes back as | A | picker list with a respawn-prefab field |
| RegrowDays | S | 14 | Days before a mined ore node comes back | **E** | named in the brief; the one number a server owner sets |
| CheckIntervalSec | S | 60 | How often the server looks for nodes to bring back | A | perf timer |
| MinPlayerDistance | S | 64 | How far a player must be for a node to pop back (m) | A | |
| MaxPerTick | S | 5 | Most nodes brought back in one check | A | perf cap |
| DryRun | L·A | false | Log what would come back without doing it | D | |
| SelfTest | L·A | false | One-time headless test of ore regrowth | D | |

### [Repair] — RepairAll (5)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Repair everything at a station in one go | M | |
| Trigger | S | OnOpen | When the batch repair happens: opening the station / a key / both | **E** | 3-way picker people immediately want to change |
| ShowMessage | S | true | Show one message per repair batch | A | cosmetic |
| Hotkey | L | `R` | Key that repairs everything at an open station | A | keybind |
| SelfTest | L·A·R | false | Log a one-time proof of the repair-eligibility rules | D | |

### [SafeSlots] — backups and the server vault (6)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Back up your extra-slot items, locally and to the server | M | |
| CharacterBackup | L | true | Copy your character file before this mod first changes it | **E** | named in the brief (safety/backups); the one thing people want to confirm is on |
| KeepBackups | L | 3 | How many character backups to keep | A | |
| VaultVersions | S | 5 | How many saved versions the server keeps per character | A | |
| MinUploadIntervalSec | S | 30 | Shortest gap between vault uploads from one player | A | perf/traffic |
| SelfTest | L·A·R | false | Prove the server vault works with no players online | D | |

### [SeaLegs] — crew sailing (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | With a crew aboard, the ship points closer into the wind | M | |
| Crew2Cone | S | 32 | How close to the wind you can point with 2 crew (degrees) | A | one of three that move together |
| Crew3Cone | S | 27 | …with 3 crew | A | |
| Crew4Cone | S | 23 | …with 4+ crew | A | |
| MaxCrewCounted | S | 4 | How large a crew keeps helping (1 turns the whole thing off) | A | the de-facto on/off, duplicating `Enabled` |
| PassengerWindGauge | L | true | Show the wind arrow to passengers who are not steering | A | UI nicety |
| SelfTest | L·A | false | Log the wind-cone table for every crew size at load | D | |

### [ServerKeys] — world-wide modifiers, all admin-only (12)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Let the mod set world-wide rules (skill rates, free building, …) | M | |
| ApplyOnLoad | S·A | true | Re-apply these every time the world loads | A | plumbing |
| RatesWithoutWorldKeys | S·A | true | Apply skill rates without flagging the world as "cheated" (keeps achievements) | A | correctness switch — should stay on; explain, don't offer |
| SkillGainRate | S·A | 1.0 | How fast everyone gains skill levels | **E** | "XP rate" — the single most-requested server setting |
| SkillReductionRate | S·A | 1.0 | How much skill you lose when you die | **E** | the death-penalty dial |
| NoBuildCost | S·A | false | Building costs nothing | A | creative-mode switch; overrides every discount knob |
| NoCraftCost | S·A | false | Crafting costs nothing | A | creative-mode switch |
| AllPiecesUnlocked | S·A | false | Every building piece unlocked from the start | A | creative-mode switch |
| NoWorkbench | S·A | false | Building needs no nearby crafting station | A | creative-mode switch |
| AllRecipesUnlocked | S·A | false | Every crafting recipe unlocked from the start | A | creative-mode switch |
| DeathKeepEquip | S·A | false | Keep your equipped gear when you die | **E** | a headline death-rule choice, and easy to explain |
| RemoveKeys | S·A | "" | Delete named world keys from the world | D | explicitly a repair tool for a bad key (the 1.0 GlobalKeys renumbering) |

### [SettingsMenu] (2)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Add the NoVikingLeftBehind tab to Valheim's settings menu | M | turning it off removes the only UI |
| ShowUnavailable | L | true | List settings you cannot change, greyed out with the reason | A | **this is the natural home for the new Simple/Advanced control** |

### [Settlement] — SettlementDiscount (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Build pieces get cheaper as the world clears bosses | M | needs `[Discount] Enabled` on at startup |
| PerTierDiscount | S | 0.10 | How much cheaper building gets per boss killed | A | compounds with 4 other build-cost knobs → preset |
| MaxDiscount | S | 0.50 | Biggest total settlement discount | A | compounds |
| MinAmount | S | 1 | Fewest resources a discounted requirement can cost | A | floor, duplicates `[Discount] MinAmount` |
| ExcludePieces | S | "" | Pieces that never get this discount | A | picker list |
| OnlyCategories | S | "" | Which build-menu categories get it (empty = all) | A | |
| SelfTest | L·A·R | false | Price five real pieces in the log | D | |

### [SkillCatchup] — GroupSkillCatchup (6)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Lift skills that are behind the group's best towards it | M | |
| ReportSec | S | 60 | How often a player's skill levels are reported | A | timer |
| WindowDays | S | 14 | How many recent days count toward the group ceiling | A | |
| BroadcastSec | S | 300 | How often the group ceiling is re-sent | A | timer |
| Bonus | S | 1.0 | How strong the skill catch-up is | A | compounds with `[Playtime] MaxBonus` and `[ServerKeys] SkillGainRate` → expose one |
| MaxFactor | S | 3.0 | Highest possible skill-gain multiplier | A | safety cap |

### [Slots] — ExtraSlots (16)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Extra inventory rows and dedicated equipment slots | M | **off → items evacuate to the bag; back on → they return (0.8.7)** — worth a warning on the row |
| EquipmentSlots | S | true | Four dedicated slots for head / chest / legs / cape | **E** | |
| UtilitySlots | S | 2 | Extra belt-type slots (0–4) | **E** | named in the brief ("extra slots count") |
| FoodSlots | S | 3 | Dedicated food slots (0–3) | **E** | |
| AmmoSlots | S | 3 | Dedicated ammo slots (0–4) | **E** | |
| QuickSlots | S | 0 | Hotkeyed quick-use slots (0–8) | A | useless until `QuickSlotKeys` is also set — a trap; keep them together in Advanced |
| GenericSlots | S | 2 | Plain extra storage slots (0–8) | **E** | |
| AutoEatFromFoodSlots | S | true | Eat from a food slot automatically when a buff runs out | A | taste knob |
| StackAllProtectsSlots | S | true | "Stack all" leaves your extra slots alone | A | correctness switch; leave on |
| QuickSlotKeys | L | "" | Keys for the quick slots, in order | A | pairs with QuickSlots |
| ShowUI | L | true | Show the extra slots in their own panel | A | cosmetic |
| PanelOffsetX | L | 0 | Move the extra-slot panel sideways | A | cosmetic |
| PanelOffsetY | L | 0 | Move the extra-slot panel up/down | A | cosmetic |
| PanelScale | L | 1.0 | Size of the extra-slot panel | A | cosmetic |
| Diagnostics | L | false | Log every extra-slot equip decision | D | |
| SelfTest | L·A·R | false | Prove the extra-slot storage with no players online | D | bound by SlotsSelfTestModule |

### [SlotsSelfTest] (1)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Run the extra-slot storage diagnostic module | M | diagnostic-only module |

### [Smelting] — RichSmelting (3)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Old-tier metal comes out richer | M | |
| OutputMultiplier | S | 2 | How many bars a smelter gives per ore, for old-tier metal | **E** | one number, very visible |
| RecipeYieldMultiplier | S | 2 | How much more an old-tier crafting recipe yields | A | secondary |

### [StackInsert] (4)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Hold a key and Use to put a whole stack into a smelter | M | |
| Modifier | L | `LeftShift` | Key held with Use to insert a whole stack | A | keybind; deliberately cfg-only (Valheim's keyboard page has no held-modifier concept) |
| MaxPerPress | S | 0 | Most items one press may insert (0 = no limit) | A | |
| SelfTest | L | false | Log a one-time check of the stack-insert maths | D | note: this one is **not** `.Admin()` — inconsistent with every other SelfTest |

### [Status] (1)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Register the `nvlb.status` console command | M | diagnostic-ish module, but harmless; module toggle |

### [Tiers] — shared material map (1, no Enabled)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| MaterialTiers | S | 47 `Prefab:tier` pairs, tiers 1–6 | Which material belongs to which age of the game | A | the foundation every catch-up feature reads; big, and wrong edits break several modules quietly |

### [Tools] — OverkillTools (7)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Late-game tools chew through early-game trees and rocks | M | |
| PerTierBonus | S | 0.5 | Extra damage per tier your tool outclasses the target by | **E** | one number, felt every session |
| MaxMultiplier | S | 3.0 | Hard cap on that bonus | A | safety cap |
| AffectTrees | S | true | Apply it to trees and logs | A | |
| AffectRocks | S | true | Apply it to mineable rocks | A | |
| AffectDestructibles | S | true | Apply it to bushes and small rocks | A | |
| SelfTest | L·A | false | Log a table proving the tool-tier maths | D | |

### [Trader] — TraderStock (3)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Traders also sell old-tier metal bars | M | |
| Items | S | `Bronze:5:60,Iron:5:80,Silver:5:120,BlackMetal:5:150` | Which old-tier items a trader stocks, and at what price | A | picker list with stack + price fields |
| TraderNames | S | `Haldor` | Which traders get the extra stock (`*` = all) | A | picker list |

### [Vanguard] — VanguardShadow (12)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Less damage, more XP and stamina near a better-geared friend | M | |
| Radius | S | 20 | How close you must be to that friend (m) | A | |
| TierGap | S | 1 | How far ahead their gear must be | A | |
| UpdateSec | S | 5 | Seconds between proximity checks | A | timer |
| DamageReduction | S | 0.25 | How much incoming damage is removed | **E** | the strength of the mod's signature buff |
| XpBonus | S | 0.5 | Extra skill XP while the buff is on | A | compounds with 3 other XP knobs |
| StaminaRegen | S | 0.2 | Extra stamina regeneration while the buff is on | A | |
| RequireBehindFrontier | S | true | Only help players whose gear is behind the world's progress | A | gate |
| IncludeUtility | S | false | Count the utility slot toward gear tier | A | scoring detail |
| NameHeuristic | S | true | Guess an item's tier from its name when there is no recipe | A | modded-content fallback |
| IconFrom | S·R | `Rested` | Which status effect to borrow the buff icon from | A | cosmetic/internal, and needs a restart |
| SelfTest | L·A·R | false | Run gear-tier self tests on a dedicated server | D | |

### [Workbench] — WorkbenchReach (6)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S | true | Workbench range grows with world progress and bench level | M | |
| PerTierMetres | S | 2 | Extra build range per boss killed (m) | A | |
| PerLevelMetres | S | 6 | Extra build range per bench upgrade (m) | A | |
| MaxRangeMetres | S | 60 | The furthest a workbench can ever reach (m) | **E** | one number, the cap everyone actually cares about |
| Stations | S | `piece_workbench` | Which stations get the extended range | A | picker list |
| SelfTest | L·A | false | Log the full range table per station | D | |

### [World] — WorldSelfTest (2)

| Key | S/L | Default | Meaning | Tier | Why |
|---|---|---|---|---|---|
| Enabled | S·A | true | Run the World diagnostic module | M | diagnostic-only module |
| SelfTest | L·A | false | Log what the World modules would do at world load | D | |

### Section row counts (sums to 294)

Access 6 · AmmoHud 6 · Builders 22 · Carry 4 · Catchup 1 · Chests 17 · ChestsSelfTest 1 ·
CorpseRun 36 · Debug 2 · Discount 5 · Economy 2 · Fires 5 · Fists 3 · Food 6 · Frontier 3 ·
General 3 · Load 7 · Loadouts 7 · Lookout 4 · Mining 6 · PickerSelfTest 2 · Playtime 7 ·
Portals 5 · Powers 18 · Recharge 7 · Regrowth 8 · Repair 5 · SafeSlots 6 · SeaLegs 7 ·
ServerKeys 12 · SettingsMenu 2 · Settlement 7 · SkillCatchup 6 · Slots 16 · SlotsSelfTest 1 ·
Smelting 3 · StackInsert 4 · Status 1 · Tiers 1 · Tools 7 · Trader 3 · Vanguard 12 ·
Workbench 6 · World 2 = **294** ✔

### Not part of the 294: the SmoothServer panel (5 foreign rows)

Built on demand from a hand-written allowlist in `src/Access/SmoothServerBridge.cs`, never in the
`ConfigCatalog`; ids carry the `SS:` prefix. They already have hand-written labels and are already
the right shape for a Simple view.

| Id | Label (already set) | Type | Tier | Note |
|---|---|---|---|---|
| `SS:Profiles.Profile` | Network preset | picker: Default / FastLink / Custom | **E** | the only network knob most servers need |
| `SS:Compression.Enabled` | Compression | toggle | A | preset-driven; refused unless preset is Custom |
| `SS:Map.Enabled` | Shared map | toggle | **E** | a real group decision |
| `SS:SmoothMotion.Enabled` | Smooth motion (this PC) | toggle (local) | A | per-machine rendering taste |
| `SS:General.EnforceClientMod` | Require the client mod | toggle, admin | A | duplicates NVLB's own `[General] EnforceClientMod` in meaning — see overlaps |

---

## 2. Proposed Simple view — 37 candidate controls under 7 headings, trimmable to 28

Player-facing labels only: no section names, no prefab words, no "multiplier". Control type in
brackets. `[!]` marks a row whose current label or hint I think is misleading — see the table at
the end of this section. The 37 are: the 33 settings tiered **Essential** in §1, plus
`[Chests] Enabled` (a module toggle that belongs here), plus the derived preset (§4), plus the two
SmoothServer rows. My trim to 28 is at the bottom.

**Progression & XP**
1. **How fast you learn skills** — `[ServerKeys] SkillGainRate` · number (0–10, 1 = normal)
2. **Skill lost when you die** — `[ServerKeys] SkillReductionRate` · number (0–5, 1 = normal) `[!]`
3. **Keep your gear when you die** — `[ServerKeys] DeathKeepEquip` · toggle
4. **Help for players who are behind** — `[Playtime] MaxBonus` · number (0–5, 0 = off) `[!]`
5. **Protection near a better-equipped friend** — `[Vanguard] DamageReduction` · number (0–1)
6. **Forsaken powers you can carry at once** — `[Powers] Slots` · number (1–3)

**Inventory & carrying**
7. **Carry weight** — `[Carry] BaseCarryWeight` · number (300–1000, vanilla 300)
8. **Extra from the Megingjord belt** — `[Carry] BeltBonus` · number (0–1000, vanilla 150)
9. **Dedicated gear slots (head, chest, legs, cape)** — `[Slots] EquipmentSlots` · toggle
10. **Food slots** — `[Slots] FoodSlots` · number (0–3)
11. **Ammo slots** — `[Slots] AmmoSlots` · number (0–4)
12. **Belt/utility slots** — `[Slots] UtilitySlots` · number (0–4)
13. **Spare storage slots** — `[Slots] GenericSlots` · number (0–8)
14. **Weapon loadouts you can save** — `[Loadouts] Slots` · number (0–4) `[!]`
15. **Repair everything at a station** — `[Repair] Trigger` · picker (When you open it / On a key / Both)

**Crafting from storage**
16. **Craft and build from nearby chests** — `[Chests] Enabled` · toggle
17. **How far a chest can be** — `[Chests] Range` · number (0–100 m)

**Building costs**
18. **Building generosity** — *derived preset* · picker (Vanilla / Relaxed / Generous / Custom) — see §4
19. **Old-gear recipes cost less** — `[Discount] CostMultiplier` · number (0.01–1, 1 = normal price) `[!]`
20. **Building gets cheaper the more you build** — `[Builders] SkillEnabled` · toggle
21. **Furthest a workbench reaches** — `[Workbench] MaxRangeMetres` · number (10–200 m)

**Gathering & the world**
22. **Mining speed on old ore** — `[Mining] SpeedMultiplier` · number (1–10)
23. **Extra ore per node** — `[Mining] DropMultiplier` · number (1–5)
24. **Bars per ore when smelting old metal** — `[Smelting] OutputMultiplier` · number (1–10)
25. **Late-game tools clear old material faster** — `[Tools] PerTierBonus` · number (0–3) `[!]`
26. **Days before mined ore comes back** — `[Regrowth] RegrowDays` · number (0–60, 0 = instantly)
27. **Carry ore through portals** — `[Portals] AllowBehindFrontier` · toggle `[!]`
28. **Fires burn this many times longer** — `[Fires] FuelDurationMultiplier` · number (1–20)
29. **Food keeps its full benefit** — `[Food] KeepFraction` · number (0–1) `[!]`

**Death & getting back**
30. **Compass to where you died** — `[CorpseRun] CompassEnabled` · toggle
31. **Free food when you respawn** — `[CorpseRun] RespawnFoodEnabled` · toggle

**Server & safety** *(admin rows greyed for non-admins)*
32. **Who may change these settings** — `[Access] TweakAccess` · picker (Everyone / Admins)
33. **Tell the server when something changes** — `[Access] Announce` · picker (Chat / On screen / Both / Off)
34. **Everyone must have this mod installed** — `[General] EnforceClientMod` · toggle
35. **Back up your character file** — `[SafeSlots] CharacterBackup` · toggle
36. **Network preset** — `SS:Profiles.Profile` · picker (Default / FastLink / Custom)
37. **Shared map for everyone** — `SS:Map.Enabled` · toggle

That is 37 controls, including the two SmoothServer rows and the derived preset. To land inside the
20–30 target, the trim list in priority order is: move **4** (catch-up strength — covered by the
optional preset in §4b), **5** (Vanguard damage reduction), **6** (Forsaken power slots), **14**
(weapon loadouts), **15** (repair trigger), **23** (extra ore per node), **25** (overkill tools),
**31** (respawn food) and **33** (announce changes) into Advanced → **28 controls**. My
recommendation is to keep **35** (character backup) whatever else goes: it is the row that makes
people trust the mod, and it costs one checkbox.

### Label/hint problems flagged above

| Setting | Current label/hint | Problem | Suggested |
|---|---|---|---|
| `[ServerKeys] SkillReductionRate` | "How much skill is lost when a player dies" | Reads like an amount but it is a *multiplier on vanilla's loss*; 0 = lose nothing, 1 = normal. People set it to 0.5 expecting "half a level". | "Skill lost when you die (1 = normal, 0 = none)" |
| `[Playtime] MaxBonus` | "Biggest possible gathering and XP bonus" | "1.0" meaning "up to double" is not guessable. | "Help for players who are behind (1 = up to double)" |
| `[Loadouts] Slots` | "How many weapon loadouts each player gets - 1 by default, 2 for a second" | Hint contains its own gotcha in code-speak, and the second *key* is separately unset — setting Slots=2 alone does nothing visible. | "Weapon loadouts you can save" + an inline note "set a key for loadout 2 below" |
| `[Discount] CostMultiplier` | "Cost of a trailing-tier recipe or build piece, as a fraction of vanilla" | "trailing-tier" and "fraction of vanilla" are both mod jargon. Also the direction is inverted (lower = more generous), which no other row does. | "Old-gear recipes cost this much (1 = full price, 0.5 = half)" |
| `[Tools] PerTierBonus` | "Extra damage per tool tier above the target's required tier" | Three pieces of jargon in one line. | "Late-game tools clear old trees and rocks faster" |
| `[Portals] AllowBehindFrontier` | "Let ore behind the frontier ride a portal" | "the frontier" is an NVLB-only concept that appears in ~15 hints. | "Carry old ore (copper, tin, iron…) through portals" |
| `[Food] KeepFraction` | "Lowest fraction eaten food keeps before expiring" | Double negative in effect; 1.0 (the default) means "no decay at all", which the wording hides. | "Food keeps its full benefit until it runs out (1 = yes)" |
| `[Frontier] TiersBehind` | "How far behind the frontier a material must be to get catch-up help" | Every catch-up module silently depends on it; changing it retunes ~8 modules at once. | Keep in Advanced, but add "changes what counts as 'old' for every catch-up feature" |
| `[Powers] Slot1Modifier` | "Obsolete - replaced by SecondSlotModifier and ThirdSlotModifier" | Bound and **ignored**; it should not be in any list. | Hide unconditionally (not even under Diagnostics) |
| `[SeaLegs] MaxCrewCounted` | "How much crew keeps helping the ship point into the wind" | `1` silently means "feature off", duplicating `Enabled`. | Note "1 = off" in the hint |
| every `.Restart()` row | greyed with no reason given | The tab greys them but the "why" is only in the long description. | Show "needs a restart" on the row |
| every `Enabled` **on**-direction | — | Flipping a module back on does nothing until restart — the single most confusing behaviour in the tab. | Show "restart needed to turn back on" the moment a module is switched off |

---

## 3. Duplicates and overlapping knobs

### 3.1 Build cost — five knobs that multiply

Per `docs/MODULES.md` (BuildersGuild / SettlementDiscount / TrailingTierDiscount), the price of a
build piece is computed **once**, in `TrailingTierDiscount`'s single requirement-scaling context:

```
final = round( tierMult × stationMult × settlementFactor × yardMult × builderSkill × rhythm )
        floored at MinAmount / Builders.MinAmounts
        — unless the framing rule replaces it outright with StructuralFirstCost/AttachedCost
        — unless [ServerKeys] NoBuildCost, which makes it free
```

where
`tierMult` = `[Discount] CostMultiplier` (+ `ExtraPerTierBehind` per further tier) ·
`stationMult` = `[Discount] StationMultipliers` ·
`settlementFactor` = `max(1 − [Settlement] PerTierDiscount × worldTier, 1 − [Settlement] MaxDiscount)` ·
`yardMult` = `[Builders] Materials` (per material, inside `YardRadius`, optionally scaled by
`StationLevelScaling`) · `builderSkill` = `1 − [Builders] SkillMaxDiscount × level/100` ·
`rhythm` = up to `[Builders] RhythmMax` in `RhythmPerRepeat` steps.

At defaults, a behind-the-frontier stone wall in a yard at Builder level 50 with a 5-piece streak
pays `0.5 × 0.70 (tier 3) × 0.5 × 0.85 × 0.80 ≈ 0.12` of vanilla — **an 88 % discount nobody
chose**. The Simple view must not expose more than one of these.

**Expose exactly one**: `[Discount] CostMultiplier`, relabelled "Old-gear recipes cost this much".
It is the only one that also covers *crafting* recipes, it is the knob Matt already tunes live, and
it is a single number with an obvious direction. Everything else goes under Advanced, grouped and
prefaced with a one-line explanation of the chain.

### 3.2 Skill XP — four knobs that multiply

`[ServerKeys] SkillGainRate` (everyone, always) × `[Playtime] MaxBonus` (+`XpBonusEnabled`) ×
`[SkillCatchup] Bonus`/`MaxFactor` × `[Vanguard] XpBonus`. A player who is behind, near a
better-geared friend, on a server with SkillGainRate 2 can be at 2 × 2 × 3 × 1.5.
**Expose `SkillGainRate` only.**

### 3.3 Carry weight — three

`[Carry] BaseCarryWeight` (cap) + `[Carry] BeltBonus` (cap while belted) vs
`[Load] WeightMultiplier` (makes materials weigh less, near a bench). Different mechanisms, same
felt effect. Expose the two `[Carry]` numbers; `[Load]` is Advanced.

### 3.4 "Behind the frontier" — four gates

`[Frontier] TiersBehind` (global), `[Portals] ExtraTiersBehind` (portals only),
`[Vanguard] TierGap` (gear gap), `[Tiers] MaterialTiers` (the map itself). Expose none in Simple;
in Advanced, put all four under one "What counts as old gear" heading.

### 3.5 Straight duplicates / near-duplicates

| | |
|---|---|
| `[Discount] MinAmount` vs `[Settlement] MinAmount` vs `[Builders] MinAmounts` | three floors on the same computed number; `[Builders] MinAmounts` (per material) wins. Show one. |
| `[General] EnforceClientMod` vs `SS:General.EnforceClientMod` | same name, two mods, two meanings. The Network panel's row must keep its "Require the client mod" label and its "(network mod)" qualifier or the two are indistinguishable. |
| `[SeaLegs] MaxCrewCounted=1` vs `[SeaLegs] Enabled=false` | two ways to switch the same feature off. |
| `[Fires] InfiniteFuel` vs `[Fires] FuelDurationMultiplier` | the toggle makes the number meaningless. Grey the number when the toggle is on. |
| `[Mining] OreNodes` vs `[Regrowth] Prefabs` vs `[Tiers] MaterialTiers` | three overlapping lists of ore prefabs, each with its own tier column; editing one and not the others is the most likely way to break this mod. |
| `[CorpseRun] RespawnFoods` vs `RespawnFoodsByFrontier` | the first, when non-empty, silently overrides the second at every tier. |
| `[Slots] QuickSlots` vs `QuickSlotKeys` | either alone does nothing. |
| the 23 `SelfTest` flags | one concept, 23 rows, 22 of them `.Admin()` and `[StackInsert] SelfTest` inconsistently not. A single "Run diagnostics" section (or one master flag) would remove 23 rows from every list. |

---

## 4. Proposed derived preset: "Generosity"

A single Simple-view picker that writes several underlying values at once. Shown as
**Vanilla / Relaxed / Generous / Custom**; it reads back as **Custom** whenever the live values do
not match any preset exactly (the same trick SmoothServer's `Profile` already uses, so the pattern
is already in the codebase and already understood by the tweak door).

I recommend **two** presets rather than one global one, because build cost and progression speed are
independent decisions people make differently — but if only one is wanted, use *Building*.

### 4a. "Building generosity" (the one I'd ship)

| Setting | Vanilla | Relaxed *(default, = today's shipped values)* | Generous |
|---|---|---|---|
| `[Discount] CostMultiplier` | 1.0 | 0.5 | 0.35 |
| `[Discount] ExtraPerTierBehind` | 0.0 | 0.0 | 0.05 |
| `[Settlement] PerTierDiscount` | 0.0 | 0.10 | 0.15 |
| `[Settlement] MaxDiscount` | 0.0 | 0.50 | 0.65 |
| `[Builders] Materials` | all `:1.0` | all `:0.5` | all `:0.35` |
| `[Builders] SkillMaxDiscount` | 0.0 | 0.30 | 0.40 |
| `[Builders] RhythmMax` | 0.0 | 0.25 | 0.35 |
| `[Builders] StructuralAttachedCost` | *(unchanged, 0)* | 0 | 0 |

Vanilla is genuinely vanilla pricing without switching any module off, so a server can be strict
without losing the yard tooltip, the Builder skill display or the refund bookkeeping.

### 4b. "Catch-up strength" (optional second preset)

| Setting | Off | Normal *(= today's shipped values)* | Strong |
|---|---|---|---|
| `[Playtime] MaxBonus` | 0.0 | 1.0 | 2.0 |
| `[SkillCatchup] Bonus` | 0.0 | 1.0 | 2.0 |
| `[SkillCatchup] MaxFactor` | 1.0 | 3.0 | 4.0 |
| `[Vanguard] XpBonus` | 0.0 | 0.5 | 1.0 |
| `[Vanguard] DamageReduction` | 0.0 | 0.25 | 0.40 |
| `[Mining] DropMultiplier` | 1.0 | 1.0 | 2.0 |
| `[Smelting] OutputMultiplier` | 1 | 2 | 3 |

A preset write is `N` tweak-door requests in order, exactly like the existing
`Save changes (N)` batch — so audit lines, `Undo` and the cfg backup all keep working unchanged,
and a preset can be undone one row at a time. Deliberately **not** included in either preset:
`[ServerKeys] SkillGainRate` (it is the one rate that must stay an explicit, visible admin choice)
and anything under `[ServerKeys]` at all (admin tier, and `NoBuildCost` would make every other
value meaningless).

---

## 5. Tier counts

| Tier | Rows | Notes |
|---|---|---|
| **Module on/off** (`[Section] Enabled`) | **41** | 8 admin-only (Access, ChestsSelfTest, Debug, Economy, PickerSelfTest, SlotsSelfTest, World, ServerKeys). All default **true**. All follow the same rule: off is live, on needs a restart. **6 belong to diagnostic-only modules** (ChestsSelfTest, Debug, Economy, PickerSelfTest, SlotsSelfTest, World) and should be hidden with the Diagnostics group; **1** (`[Chests] Enabled`) should additionally appear on the Simple page. Normal module list = **35**. |
| **Diagnostic** (hidden unless "Show diagnostics") | **32** | 22 × `SelfTest` + `[Access] NetworkSelfTest`, `[Regrowth] DryRun`, `[Chests] Diagnostics`, `[Slots] Diagnostics`, `[Frontier] TierOverride`, `[ServerKeys] RemoveKeys`, `[Debug] AllowTestCommands`, `[General] Mode`, `[General] HotReload`, `[Powers] Slot1Modifier` (obsolete — hide always, even from the Diagnostics group). |
| **Essential** (Simple view) | **33** | from NVLB's own 294. Simple page = 33 + `[Chests] Enabled` + 1 derived preset + 2 SmoothServer rows = **37 controls**, trimmed to **28** in §2. |
| **Advanced** (everything else) | **188** | 294 − 41 − 32 − 33 |
| **Total** | **294** | ✔ |

Effect on the tab: today every player opening Escape → Settings → NoVikingLeftBehind sees 41 module
rows and 294 settings. Under this split the default page is **~28 rows**, Advanced holds 188
settings + 35 module toggles behind one click, and 32 diagnostic settings plus 6 diagnostic module
toggles disappear entirely unless a "Show diagnostics" checkbox is ticked.

---

## 6. Implementation notes for whoever builds this

- The tier is a **new field on `Opt`** (`Opt.Simple()` / default Advanced / `Opt.Diag()`), set at
  the ~254 bind sites. That keeps one description per setting, which is the property the whole
  catalog design rests on (`ConfigCatalog`, `nvlb.catalog`, the tweak door and the tab all read the
  same object). Nothing else needs to change: `SettingInfo` gains one field, `NvlbSettingsTab`
  gains one filter.
- **Do not** add a range to a setting that does not have one as part of this work — ranges are
  advisory here on purpose (`Opt.cs` comment: BepInEx clamps on load and would silently rewrite a
  server's cfg), and adding one changes what the tweak door refuses.
- The Simple/Advanced/Diagnostics choice is itself a setting: put it next to
  `[SettingsMenu] ShowUnavailable` as a local `[SettingsMenu] View` (`Simple` | `Advanced`) plus
  `[SettingsMenu] ShowDiagnostics` (bool, local, default false). That is +2 settings → 296.
- The derived preset needs no new storage: read-back is "do the live values match a preset row",
  which is exactly `SmoothServerBridge`'s `ProfileDriven` check, and the write is an ordered batch
  through `TweakDoor.Request`, which the tab already does for `Save changes (N)`.
