# Docks plan (waterproofing and building over water)

Matt, verbatim:

> "Waterproofing wood. I know there exists mods for this, but I don't know if making it an extra
> step of applying resin to the wood is really QoL. Maybe we could explore a couple different ideas
> for ways to approach this. When building a boat dock I quickly realized how frustrating it is when
> building supports underwater and stairs that lead into the water. Not to mention just how
> difficult it is to literally just get the supports to connect to the ocean floor..."

Research only; nothing is built. Every line number below is from the 1.0.15 decompile on the box,
`/opt/modlab/game/_dump/b25390671/assembly_valheim` (build 25390671 = `Version.cs:168`,
`new GameVersion(1, 0, 15)`, the public branch since 2026-09-18 12:48 UTC).

## 1. Vanilla 1.0.15, the exact members involved

**Water wear.** `WearNTear.UpdateWear(float)` (`WearNTear.cs:523`) runs only on the ZDO owner and
only inside the active area. Line 541 sets `m_rainWet = !shielded && !m_haveRoof && m_noRoofWear &&
EnvMan.IsWet()`. Line 547 is the whole wet rule:

```csharp
if (m_noRoofWear && !flag && GetHealthPercentage() > 0.5f)   // flag = inside a ShieldGenerator
    if (IsWet()) { ... every 60s: num += 5f; }                 // 5% of m_health per minute
```

`IsWet()` (`WearNTear.cs:800`, public) is `m_rainWet || IsUnderWater()`, and `IsUnderWater()`
(`WearNTear.cs:1014`, private) is `Floating.IsUnderWater(transform.position, ref
m_previousWaterVolume)`. **Rain and submersion are already separate values inside `IsWet`**, and
`IsUnderWater()` has exactly one caller, `IsWet()`. That is the whole waterproofing hook.

The health floor is **50%**, and it is the literal `0.5f` in the line-547 condition, not a field.
Rain and water share it. Damage is `num / 100f * m_health` applied through `ApplyDamage`
(`WearNTear.cs:688`), suppressed world-wide by the `NoBuildingFall` global key.

`m_noRoofWear` (`WearNTear.cs:62`) and `m_noSupportWear` (`:66`) default to `true` on the component;
what stone and iron pieces actually ship is a per-prefab serialized value we cannot read from the
assembly. **So "stone is immune already" is unverified** and the module must scope by
`m_materialType` (`:77`) explicitly rather than trust the flag. Self-test 1 dumps the truth.

**Support and the seabed.** `WearNTear.UpdateSupport()` (`:820`) overlaps each bound with
`s_rayMask`, and `s_rayMask` (`:283`) is `piece, Default, static_solid, Default_small, terrain`.
Two facts fall out. Line 902: any collider on `s_terrainLayer` sets `flag`, and line 944 then sets
`m_support = GetMaxSupport()` and returns, so **a pole whose bounds touch the seabed gets full
support, exactly as one on dry land**. And **the Water layer is not in `s_rayMask` at all**, so the
water surface can never grant support. `GetMaterialProperties` (`:1359`) gives Wood max 100 / min 10,
HardWood 140/10, Timberwood 200/10, Stone 1000/100, Iron 1500/20. `HaveSupport()` (`:1009`) is
`m_support >= GetMinSupport()`.

**Placement.** `Player.m_placeRayMask` (`Player.cs:668`) is `Default, static_solid, Default_small,
piece, piece_nonsolid, terrain, vehicle`. **No Water layer.** `m_placeWaterRayMask` (`:669`) adds it
and is used only when the piece is `m_waterPiece` or `m_noInWater` (`UpdatePlacementGhost`,
`Player.cs:3795`). So for a wood pole the camera ray already passes straight through the water
surface and hits the seabed. `PieceRayTest` (`:4217`) then gates on
`Vector3.Distance(m_eye.position, hitInfo.point) < m_maxPlaceDistance + piece.m_extraPlacementDistance`,
and `m_maxPlaceDistance` is **5 m** (`Player.cs:171`). `UpdatePlacementGhost` (`:3783`) positions the
ghost by pushing its nearest collider point onto that hit point (`Player.cs:4010-4043`), then
auto-snaps to the closest snap-point pair within **0.5 m** (`FindClosestSnapPoints`, `:4118`, called
at `:4060`) unless AltPlace is held. Snap-to-piece therefore already beats snap-to-floor, because
the snap pass runs after the ray-derived position and overwrites it.

**Place path.** `UpdatePlacement` place branch (`Player.cs:1373-1420`): `HaveStamina`, then
`HaveRequirements(piece, CanBuild)`, then `TryPlacePiece` (`:3031`), and only then
`ConsumeResources(piece.m_resources, 0)` (`:2884`, signature carries a `multiplier`) and
`UseStamina(GetBuildStamina())`. `PlacePiece(piece, pos, rot, doAttack, cheated)` (`:3089`) is public
and never reads the ghost. Identical to what FARMING-PLAN builds its grid on.

**Swimming.** `Character.IsSwimming()` (`Character.cs:3522`) is `m_swimTimer < 0.5f`, set whenever
liquid depth exceeds `m_swimDepth - 0.4f` with `m_swimDepth = 2f` (`Character.cs:167`). Two gates
stop the hammer, both in `Humanoid`, neither in `Player`: `UpdateEquipment` (`Humanoid.cs:383`)
calls `HideHandItems()` every frame while `IsSwimming() && !IsOnGround()`, and `EquipItem`
(`Humanoid.cs:1069`) refuses outright under the same test. `Player.UpdatePlacement` itself has no
swim check. Stamina regen is zeroed while swimming (`Player.cs:2105`).

**Water surface and terrain queries.** `Floating.GetLiquidLevel(Vector3, float, LiquidType)`
(`Floating.cs:235`) and `Floating.IsUnderWater(Vector3, ref WaterVolume)` (`:318`), both static and
both cheap (`OverlapSphereNonAlloc` at radius 0 on the WaterVolume mask). Sea level is
`ZoneSystem.c_WaterLevel = 30f` (`ZoneSystem.cs:482`). Seabed: `ZoneSystem.GetGroundHeight(Vector3)`
(`:2734`) raycasts down from y=6000 on `m_terrainRayMask`, and `GetSolidHeight(p, radius, out h,
ignore)` (`:2780`) is the variant that ignores a transform, which is what a ghost needs.

**Ghost visuals.** `SetupPlacementGhost` (`Player.cs:3457`) instantiates the real prefab and
`CleanupGhostMaterials` (`:3619`) clones every material, zeroes `_ValueNoise` and sets
`_TriplanarLocalPos`. Validity colour is one call: `SetPlacementGhostValid` (`:3645`) to
`Piece.SetInvalidPlacementHeightlight(bool)` (`Piece.cs:462`), which is
`MaterialMan.instance.SetValue(gameObject, ShaderProps._Color / _EmissionColor, Color.red)` and
`ResetValue` to clear. The ghost is hard to read under water because nothing tints it and the water
volume renders in front of it with depth fog; the piece itself is drawn normally.

**Snap points.** `Piece.GetSnapPoints(List<Transform>)` (`Piece.cs:618`) is just "every child
tagged `snappoint`". There is no height field on a piece, so a pole's length has to be derived from
its own snap points. **Pole heights are not readable from the assembly** (they are prefab geometry,
and there is no asset dump on the box). The commonly cited 2 m `wood_pole` / 4 m `wood_pole2` is
stated here as unverified; the design never hardcodes it and measures the prefab instead.

## 2. Prior art

| Mod | What / how it hooks | Verdict |
| --- | --- | --- |
| **ResinGuard** (Azumatt) | Craft resin (decays, 3600 s) or tar and apply per piece; `damage *= 1 - resin/max`. ServerSync. | **This is exactly the extra step Matt does not want.** Also self-marked Deprecated. The one mod offering tunable rather than binary protection, and it pays for it with per-piece busywork. |
| **NoRainDamage** (JoelOliMclean) | Rain damage off for roofless pieces, filter + rate config. 230 K downloads. Only mod in the group that **explicitly claims 1.0**. | **Client-only by the author's own README**: every player in the area must install it or wear resumes. Unenforceable on a dedicated server. Wrong half of the problem anyway (rain, not water). |
| **WaterproofBuildings** (Valdream) / **DisableRainDamage** (Alpus) / **Disable Water Wear** | Blanket "water never damages structures", zero config, 3.8 KB. | All-or-nothing, no progression, no 1.0 claim; Disable Water Wear is 2 years stale on BepInEx 5.4.2202. |
| **AzuWearNTearPatches** (Azumatt) | Per-material integrity multipliers, blanket no-weather-damage, kicks clients without it. | The closest thing to server-enforced, and **Deprecated**. If it is installed, it already does more than we do: stand down. |
| **ValheimPlus `[Building] noWeatherDamage`** (Grantapher fork) | **Transpiler on `WearNTear.UpdateWear`** that zeroes the wear contribution. Historically rain/water only. Grantapher shipped 5 releases in 2 days after 1.0, last confirmed on **1.0.12**. PR #157 adds per-source opt-outs for 1.0's snow/ash/lava wear. | Confirms our patch target, and confirms the trap: a transpiler on `UpdateWear` in 1.0 sits in a method that also does snow, ash, lava and biome wear. We patch `IsUnderWater()` instead and touch none of it. |
| **HexWaterproofBuilding** (Hex_Viking) | Jotunn piece pack: parallel "waterproof" pieces plus a **4 m Vertical Pier Support that auto-extends to the seabed**, plus extended placement range. Server + all clients. | The only real dock primitive found, and it is a separate piece catalog: vanilla builds get nothing, and removing the mod strands placed pieces as non-interactable. Ours works on vanilla poles and adds no prefab. |
| **SeafloorWalkingBoots** (NexusPort) / **Underwater** (Crystal) | Walk the seabed instead of swimming, so placement works at depth. Boots need the mod server-side; Underwater is a Backspace toggle. | Both solve access, neither solves reach, cost or ghost readability. Crystal says outright the mod exists "to assist with placing building pieces underwater", which is Matt's complaint from a different direction. |
| **Infinity Hammer** (JereKuusela) | Place anything anywhere, undo, `hammer_grid`, `hammer_freeze`, generated snap points. | Admin tool, replaces the placement pipeline wholesale. Nothing water-specific. If present, stand AutoExtend down. |
| **Extra Snap Points Made Easy** (Searica) / **ComfyGizmo** | Cycle snap points on held and target piece; precision rotation. | Genuinely useful for piers and does not collide with us. Coexist, always. |
| **PlanBuild**, **ValheimRAFT** | Blueprints and terrain flattening; vehicle building and vehicle "docking". | Neither addresses a static pier over water. RAFT is explicitly incompatible with Gizmo. |

The gap: **nobody ships server-enforced, progression-tied, water-only wear relief with no consumable
step, and nobody makes the ghost readable under water at all.** That is NVLB's exact shape.

## 3. The modules

**Two modules, not one.** `[Waterproof]` and `[Docks]`. They share no plumbing (one is a two-line
postfix on a `WearNTear` predicate, the other is a client placement feature the size of
FARMING-PLAN's grid), they want different `Side` values, and a bad hammer patch must not be able to
take waterproofing down with it, which is the whole point of `FeatureModule`'s per-module Harmony
instance. Matt can also hand out weather relief without changing how anyone builds.

### 3.1 `[Waterproof]`, Side = Both, Theme "Building & gathering"

**One postfix, on `WearNTear.IsUnderWater()`** (`WearNTear.cs:1014`, private, one caller). Return
`false` when the piece qualifies. Because `m_rainWet` is computed separately at `:541` and `IsWet()`
ORs the two, suppressing the underwater half leaves rain, roofs, `HaveRoof`, `m_noRoofWear`, the
wet visual, the `ShieldGenerator` check, snow, ash, lava and biome wear **byte-for-byte vanilla**.
No transpiler, so a future patch that reshuffles `UpdateWear` cannot silently break us the way it
would break a ValheimPlus-style transpile.

The tier is the health floor, and vanilla already has one:

```ini
WaterWearFloorByTier = 0:50,1:60,2:70,3:80,4:90,5:100,6:100,7:100
```

Read as "water wear stops once the piece is at or below this percentage of full health". Tier 0 is
**50, which is vanilla's own `0.5f`**, so tier 0 is exactly today's game and the value is clamped to
a minimum of 50 so the setting can never make wear worse. Tier 5 and beyond is 100, meaning water
never damages the piece at all. The postfix is:

```
if (!Live()) return;
if (!MaterialCovered(__instance.m_materialType)) return;
if (__instance.GetHealthPercentage() <= Floor(Frontier.WorldTier)) __result = false;
```

A `[Tiers]`-shaped string, not a `Tier` + `Floor` pair, for the same reason `GridByTier` is one:
Matt wants the curve, not a cliff, the picker and the tweak door already understand this shape, and
one string hot-reloads as a unit so a half-applied change is impossible.

**Underwater and wet-by-waves are the same thing and stay that way.** `Floating.IsUnderWater` is a
point test against the wave-displaced surface (`WaterVolume.GetWaterSurface`), so a piling in the
splash zone flickers in and out of "underwater" as waves pass. Trying to tell a submerged piece from
a splashed one would mean sampling the surface over time and inventing a threshold that vanilla
never had. The whole rule is "the sea is eating my dock", and both cases are the sea.

`Materials` (default `Wood,HardWood,Timberwood`) scopes it by `m_materialType`, because whether
stone actually ships `m_noRoofWear = false` is a prefab value we cannot read. Setting it wider is a
one-line config change, and self-test 1 prints what every piece in the game really has.

**Already-damaged pieces.** Nothing retroactive, deliberately. A dock at 40% stays at 40% until
someone swings a hammer at it; after one repair it holds at the new floor and never decays past it
again. No health is ever written by this module, so there is no migration, no ZDO touch, and turning
`[Waterproof] Enabled` off puts the world straight back on vanilla's decay curve.

### 3.2 `[Docks]`, Side = Client (Both while `SelfTest`), Theme "Building & gathering"

Four sub-toggles, the CorpseRunPlus pattern, in the order they matter.

**(a) Reach, `ReachBonusMetres`, default 3.0.** This is the real answer to "just get the supports to
connect to the ocean floor", and the first-pass guess about the water plane was wrong: `m_placeRayMask`
has no Water layer (`Player.cs:668`), so the ray already reaches the seabed. What refuses is
`Vector3.Distance(m_eye.position, hitInfo.point) < m_maxPlaceDistance` at `Player.cs:4230` with
`m_maxPlaceDistance = 5f`. Stood on a shore with 3 m of water below and 3 m out, the seabed is past
5 m and the answer is `NoRayHits`. A postfix on `PieceRayTest` cannot help (it has already decided);
the hook is a **prefix/postfix pair around `PieceRayTest` that temporarily raises the instance field
`m_maxPlaceDistance` and restores it**, gated on "the ghost position or the camera ray is over
water". This follows WorkbenchReach's rule: never mutate a shared prefab, and restore in a `finally`
so an exception cannot leave the player with permanent extra reach. Applied on land too? No: gated on
water, so ordinary building is untouched and nobody can use this as a general range cheat.

**(b) Hammer while swimming, `AllowSwimming`, default true.** Exactly two prefixes, both narrow:
`Humanoid.UpdateEquipment` (`Humanoid.cs:383`) must not call `HideHandItems()`, and
`Humanoid.EquipItem` (`Humanoid.cs:1069`) must not refuse, **only when the item is the build hammer
and the player is in place mode**. Every other item keeps vanilla behaviour, so you still cannot
swim with a sword out. Placing while treading water works because `Player.UpdatePlacement` never
tested swimming in the first place.

The drowning risk is real and is not hypothetical: stamina regen is **zero** while swimming
(`Player.cs:2105`) and swimming already drains it. Adding `GetBuildStamina()` per swing on top of
that is how a player builds themselves to 0 stamina in deep water. Guard, `SwimStaminaReserve`,
default 25: while swimming, refuse the place (message, no cost, no swing) if stamina after the spend
would drop below this. Refusing to build is recoverable; drowning is not. The reserve is a synced
number so Matt can set it to 0 for a group that would rather take the risk.

**(c) Ghost readability, `SubmergedTint`, default true.** Postfix `Player.UpdatePlacementGhost`:
when `m_placementGhost.transform.position.y < Floating.GetLiquidLevel(pos)` and the status is
`Valid`, call `MaterialMan.instance.SetValue(ghost, ShaderProps._EmissionColor, tint)`; otherwise
`ResetValue`. That is the identical mechanism `Piece.SetInvalidPlacementHeightlight` (`Piece.cs:462`)
already uses for the red invalid state, so it composes with it for free: red still wins for invalid,
because vanilla's call runs after ours in the same frame. Default tint a pale cyan at low intensity,
`SubmergedTintColor`, so the piece reads as "lit" through the water fog rather than recoloured.

Not an outline. An outline needs a replacement shader or a second camera pass, is new visual
language nobody has been taught, and would have to be kept consistent with the red-invalid state
that already exists. Emission on the existing material is one call and zero new rendering.

**(d) `AutoExtend`, default true, poles only.** Place a pole over water and the missing pieces
beneath it down to the seabed are placed too, at full price.

* **Which prefab:** the same one. No substitution, no new prefab, no new ZDO data.
* **The vertical step** comes from the ghost's own snap points, never a constant: take
  `Piece.GetSnapPoints` (`Piece.cs:618`), project to local Y, and use `maxY - minY`. A prefab whose
  snap points do not separate on Y by at least `MinStepMetres` (0.5) **is not a pole** and is
  skipped; that single test is what keeps beams, stairs and floors out without a prefab name list,
  and it is why stairs into the water are out of scope for v1 (a stair's snap points separate on Y
  *and* Z, so repeating it vertically would produce a staircase to nowhere, not a support).
* **How many:** `ceil((ghostBottomY - seabedY) / step)`, seabed from
  `ZoneSystem.GetSolidHeight(p, radius, out h, ignore: ghost.transform)` (`ZoneSystem.cs:2780`), the
  variant that ignores the ghost itself. Capped at `MaxPieces`, default 8. **If the seabed is deeper
  than the cap, place the cap's worth and stop**: the player gets a partial pier they can see and
  extend, which is strictly better than a refusal at the moment the feature is most wanted. This is
  the same "invalid cells are skipped, not fatal" rule FARMING-PLAN argues for.
* **Cost and validity:** exactly FARMING-PLAN's shape. Vanilla places the anchor and charges for it.
  A postfix on `TryPlacePiece` calls `PlacePiece(piece, pos, rot, doAttack: false)` for each extra
  that passes, then charges once with `ConsumeResources(piece.m_resources, 0, -1, placedExtras)` and
  `UseStamina(GetBuildStamina() * placedExtras)`, counted from pieces that really got placed, never
  requested. **If the inventory cannot pay for the full stack, the stack shrinks to what it can pay
  for**, from the bottom up; it never places a free piece and never half-charges.
* **Preview:** pooled real ghosts, one per extra, so the player sees the whole stack and its red
  cells before clicking, and the HUD cost row shows the total. Same argument and same pooling as
  FARMING-PLAN 3.3: the ghost is vanilla's entire placement vocabulary and an outline cannot say
  *which* piece is blocked.

**Why the structure holds** is not a guess: `UpdateSupport` grants `GetMaxSupport()` to anything
overlapping the terrain layer (`WearNTear.cs:902`, `:944`), so the bottom pole on the seabed is as
supported as one on a beach, and wood's vertical loss of 0.125/m over an 8 m stack from a 100 max
still clears the min of 10.

**Water is never treated as ground, and that needs no code.** `WearNTear.s_rayMask`
(`WearNTear.cs:283`) is `piece, Default, static_solid, Default_small, terrain` with no Water layer,
so a piece over open water can never draw support from the surface. Free-floating platforms are
already impossible. The rule for this module is simply **never add Water to that mask**, and
self-test 5 asserts it.

### 3.3 Guard rails

No new prefabs, no new ZDO keys, no custom RPC: an NVLB world still opens in vanilla. Every extra
piece goes through vanilla `PlacePiece`, so `PrivateArea` is checked per piece by vanilla's own code
inside the call, creator ID and ward flashing are vanilla's, and nothing is placed that the player
could not have placed by hand. The reach bonus is restored in a `finally` and only applies over
water. A piece is never placed that the ghost preview did not show. Nothing is placed that the
inventory did not pay for.

## 4. Settings

```ini
[Waterproof]
Enabled              = true
WaterWearFloorByTier = 0:50,1:60,2:70,3:80,4:90,5:100,6:100,7:100   # SIMPLE, Gathering 40
Materials            = Wood,HardWood,Timberwood
SelfTest             = false                                         # .Diag()

[Docks]
Enabled              = true            # SIMPLE, Building 40
ReachBonusMetres     = 3.0             # over water only; vanilla m_maxPlaceDistance is 5
AllowSwimming        = true            # SIMPLE, Building 45
SwimStaminaReserve   = 25
SubmergedTint        = true
SubmergedTintColor   = 3AC8FF
TintIntensity        = 0.35
AutoExtend           = true            # SIMPLE, Building 50
MaxPieces            = 8
MinStepMetres        = 0.5             # snap-point Y spread below this = not a pole
Coexist              = Auto            # Auto | Always | Never
SelfTest             = false           # .Diag(), flips Side to Both
```

Simple view gets four rows: the water-wear curve, the `[Docks]` toggle, hammer while swimming, and
auto-extend. Everything else is Advanced. Nothing here is admin-only; none of it is exploitable in
the way `[ServerKeys]` is.

## 5. Headless self-test

Pure functions first, so most of this runs with no world:

1. **Material and flag dump.** Every piece prefab in the hammer's tables with its `m_materialType`,
   `m_noRoofWear`, `m_noSupportWear` and `m_health`. This is the check that settles whether stone
   and iron really are immune; the answer goes into `MODULES.md` rather than being assumed.
2. **Floor table.** `WaterWearFloorByTier` parses, prints tiers 0..7, proves every value is clamped
   to `>= 50` and `<= 100`, and proves tier 0 equals vanilla's `0.5f` exactly.
3. **Floor predicate.** For health 0.0..1.0 in 0.05 steps against each tier's floor, the postfix's
   verdict matches `health <= floor`, and at tier 0 it never suppresses anything vanilla would not
   already have suppressed at line 547.
4. **Pole-count maths.** For depths 0.1..30 m and steps 1, 2, 4 m: count is `ceil(depth/step)`,
   clamped to `MaxPieces`, never negative, and `1 + extras` equals the pieces actually placed.
5. **Support mask assertion.** `WearNTear.s_rayMask` contains no Water layer bit, and
   `Player.m_placeRayMask` contains no Water layer bit. Both fail loudly if a future patch changes
   them, because both conclusions in section 3.2 depend on it.
6. **Step derivation.** For every piece in the hammer tables, the snap-point Y spread, and which
   pieces pass the `MinStepMetres` pole test. Any vanilla stair, beam or floor appearing in that
   list is a bug in the test, not a feature.
7. **Stand-down.** With a fake plugin GUID injected for ValheimPlus, AzuWearNTearPatches and
   Infinity Hammer, the right sub-features report disabled and the rest report still on.

## 6. Work breakdown

| File | Lines | Contents |
| --- | --- | --- |
| `src/Modules/Docks/WaterproofModule.cs` | ~220 | config, floor table, the `IsUnderWater` postfix, material scope, `StatusDetail` |
| `src/Modules/Docks/DocksModule.cs` | ~320 | config, sub-toggles, stand-down, hot reload, shared "is this over water" helper |
| `src/Modules/Docks/DockReach.cs` | ~120 | `PieceRayTest` pre/post, reach restore, swim gates, stamina reserve |
| `src/Modules/Docks/DockGhost.cs` | ~200 | submerged tint, pooled extra ghosts, throttled revalidation |
| `src/Modules/Docks/DockExtend.cs` | ~240 | step from snap points, depth to count, `TryPlacePiece` postfix, exact consume |
| `src/Modules/Docks/DocksSelfTest.cs` | ~200 | the seven checks above |
| `docs/MODULES.md` | +2 rows | |
| `README.md` | module count 41 to 43 | |

About 1,300 lines. Stage it: `[Waterproof]` alone first (one postfix, shippable on its own, and the
half Matt asked about first), then Docks (a) and (b), then (c), then (d).

## 7. In-game test script for Matt

1. NEWTEST, `[Frontier] TierOverride = 0`. Build a wood wall in the rain with no roof. It should
   still get the wet sheen and still decay to 50% exactly as it does today. That is the proof rain
   is untouched.
2. Drop a wood pole so its base sits under water. At tier 0 it decays to 50% and stops, vanilla.
   `TierOverride = 3` live via `cfg.py`: it now holds at 80% and stops. `TierOverride = 5`: it never
   takes water damage again. No restart at any point.
3. Repair a dock piece that is already at 30%. It should go to 100% and stay there, not slide back
   to the floor.
4. A sloped shore, water 2 to 4 m deep. Stand on the beach and aim a pole at the seabed 4 m out.
   Today that is "nothing to build on". It should now place, with the base on the seabed.
5. Same spot, hold a beam and aim under water. The ghost should read clearly through the fog, and
   turn red where it is blocked.
6. Swim out into 4 m of water and take the hammer out. It should stay in your hands. Place a pole:
   it goes in. Now swim yourself down to low stamina and try again: it should refuse with a message
   rather than let you spend the last of it.
7. Place a pole over 6 m of water. The preview should show the full stack down to the seabed and the
   HUD should show the full cost. Place it: count the wood before and after, it should be exactly
   the stack. Then repeat with only enough wood for two: you should get two, bottom-anchored, and
   nothing free.
8. Place over water deeper than 8 poles. You should get 8 and no error.
9. Put a ward down and try to auto-extend into it. Vanilla should refuse the pieces inside it.
10. `nvlb.status` should name both modules, the current floor for this tier, the derived step for the
    selected piece, and the stand-down state. Check the settings tab at 100% and 150% GUI scale.

## 8. Coexistence

`Coexist = Auto` resolves installed plugins by GUID through `BepInEx.Bootstrap.Chainloader.PluginInfos`,
the `SmoothServerBridge` pattern, no compile-time reference, one log line when it stands down.

* **ValheimPlus** with `noWeatherDamage` on, or **AzuWearNTearPatches**: both already suppress water
  wear globally and V+ does it with a transpiler on the same `UpdateWear`. Stand `[Waterproof]` down
  entirely. Two mods arguing about the same damage number is exactly the class of bug that is
  impossible to diagnose from a screenshot.
* **NoRainDamage**: coexist. It owns rain, we own water, and they meet only inside `IsWet()` where
  the two terms are already separate.
* **Infinity Hammer**: stand `AutoExtend` down. It replaces the placement pipeline and a second
  `TryPlacePiece` postfix on top of it is the "cannot use the hammer" class of bug.
* **HexWaterproofBuilding**: coexist, with a note. Its pier support is its own custom piece; ours
  works on vanilla poles, so a player can have both and will not notice.
* **ComfyGizmo, Extra Snap Points Made Easy, PlanBuild, Underwater, SeafloorWalkingBoots**: coexist,
  always. Gizmo touches rotation, the snap mods touch snap selection, and the two seabed-access mods
  make (b) redundant without conflicting with it.
* NVLB's own: `SettlementDiscount` and `TrailingTierDiscount` price the pole through
  `Piece.Requirement.GetAmount`, so an auto-extended stack is discounted like any other build, which
  is correct and needs no code. `BuildersGuild` `MinAmounts` applies per piece, so a stack of 8 at
  `Wood:0` would be free; note that in `MODULES.md` so nobody sets it by accident.

## 9. Open decisions for Matt

1. **Water-wear floor by tier (recommended) or a plain on/off?** The tiered floor makes waterproofing
   something the world earns, which is what the rest of NVLB does, and tier 0 is provably vanilla. A
   single switch is one line and needs no explaining, but it hands out the whole thing on day one.
2. **Auto-extend on by default with a held key to place a single piece (recommended), or off by
   default?** On-by-default is the FARMING-PLAN argument: someone who earned the feature should not
   have to hold a key for it. The risk is a player who wanted one pole over water and spent eight.
3. **Stairs into the water: out of scope for v1 (recommended) or worth solving now?** The snap-point
   test cleanly excludes them, but Matt named them in the brief. Doing them properly means deciding
   what a half-submerged staircase should even snap to, which is a design question, not a patch.
4. **Reach bonus over water only (recommended) or everywhere?** Water-only keeps ordinary building
   exactly vanilla and makes the setting impossible to abuse as a general range cheat. Everywhere is
   simpler to explain and is what several build mods do.
