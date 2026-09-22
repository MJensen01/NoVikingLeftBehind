# Farming plan (Reddit launch-post requests)

Two comments on the launch post, verbatim:

> "Just needs a clock (like text based one) and mass farming and it's an all in one wonder mod."

> "I've often wanted a farming mod that follows base game progression a bit. I'd start with just a
> few QoL tweaks. Reduce stamina cost of using cultivator. Allow plants to snap to a grid that
> perfectly spaces them and lines them up. Maybe some other stuff I'm not thinking of. Then tie the
> feature of being able to plant more than one at a time behind tiers. Maybe bronze does up to 2x2.
> Add an iron cultivator and it can do up to 3x3, silver 4x4, black metal 5x5, etc. Edit: or a
> simpler way to do this might be to just allow the cultivator to create bigger grids after each
> boss is killed. Seems like you already have mechanics like this."

Matt: "I like all of those ideas." The clock is separate work. Research only; nothing is built yet.

## 1. Vanilla 1.0.14, the exact members involved

Read from `/opt/modlab/game/_dump/v1_0_14/assembly_valheim` on the box.

* `Player.UpdatePlacement(bool, float)` (`Player.cs:1259`), place branch `Player.cs:1373-1398`:
  `m_buildPieces.GetSelectedPiece()`, gate on
  `HaveStamina(rightItem.m_shared.m_attack.m_attackStamina)`, gate on
  `HaveRequirements(piece, RequirementMode.CanBuild)`, call `TryPlacePiece`, and only then
  `ConsumeResources(piece.m_resources, 0)` and `UseStamina(GetBuildStamina())`. Note the order: the
  cost is taken **after** a successful place, by the caller, not by `TryPlacePiece`.
* `Player.TryPlacePiece(Piece)` (`Player.cs:3031`) calls `UpdatePlacementGhost(true)`, switches on
  `m_placementStatus` (enum `Player.cs:72`, 15 values incl. `NeedCultivated`, `WrongBiome`,
  `MoreSpace`), and on `Valid` calls `PlacePiece(piece, ghost.position, ghost.rotation, true, cheated)`.
* `Player.PlacePiece(Piece, Vector3, Quaternion, bool doAttack, bool cheated)` (`Player.cs:3089`) is
  public, takes an explicit position and rotation and never reads the ghost. That is the whole
  per-cell placement primitive we need.
* `Player.UpdatePlacementGhost(bool)` (`Player.cs:3783`) positions `m_placementGhost`
  (`Player.cs:378`, private) and sets the status; `SetupPlacementGhost()` (`Player.cs:3457`)
  rebuilds it when the selection changes.
* `Player.GetBuildStamina()` (`Player.cs:1246`): the held tool's `attackStamina`, times the home-item
  and seman modifiers, halved by the build skill factor. Both build spends (`Player.cs:1338`,
  `Player.cs:1398`) go through it.
* `Player.ConsumeResources(Piece.Requirement[], int qualityLevel, int itemQuality = -1, int multiplier = 1)`
  (`Player.cs:2884`). That `multiplier` is exactly what an N-cell place needs.
* `Plant.m_growRadius` (`Plant.cs:34`), `m_needCultivatedGround` (`Plant.cs:38`),
  `HaveGrowSpace()` (`Plant.cs:389`):
  `Physics.OverlapSphereNonAlloc(transform.position, m_growRadius, s_colliders, m_spaceMask)`,
  rejecting **any** collider that is not a `Plant` and any *other* `Plant` that is `Healthy`.
  `Plant.UpdateHealth` (`Plant.cs:212`) produces `NoSpace` / `NotCultivated` / `WrongBiome` / `NoSun`.
* `Pickable.Interact(Humanoid, bool, bool)` (`Pickable.cs:180`) rolls the `m_pickRaiseSkill` bonus
  then `m_nview.InvokeRPC("RPC_Pick", num)`; `RPC_Pick` (`Pickable.cs:235`) runs on the owner and
  drops `m_itemPrefab` x `m_amount`. `Piece.m_cultivatedGroundOnly` (`Piece.cs:147`).

## 2. Prior art

| Mod | What it does | How it hooks | Verdict |
| --- | --- | --- | --- |
| **PlantEverything** (Advize) | ~30 extra cultivator recipes, growth hover text, debris removal | prefixes `Plant.HaveRoof` and `Plant.HaveGrowSpace` to *relax* them for its own plants; ServerSync | Maintained, recompiled for 1.0. Not a competitor: it adds crops, we space them. **Coexist.** |
| **PlantEasily** (Advize) | NxN mass plant, grid snap, bulk harvest, auto-replant, red highlight on bad cells | same repo, client-side | Closest competitor and the best UX reference. **Client-only, explicitly "do not install on a dedicated server".** Needed a large perf pass (ghost updates ~56x faster) after ghost-lag complaints. Settings are not fully ServerSynced (issue #17), so a group cannot agree on a grid size. |
| **MassFarming** (Xeio) | hold-key mass plant grid, hold-key radius harvest | `Player.TryPlacePiece` pre/post, `Player.UpdatePlacement` pre/post, `Player.SetupPlacementGhost` pre/post, `Player.UpdatePlacementGhost` postfix; cell pitch = `Plant.m_growRadius * 2` | The mechanical blueprint, and the pitch rule is right. But 12 open issues, several of them "grid placement not working" (#33, #29, #7), "cannot use hammer or hoe when mod active" (#12), and the mass-plant hotkey resetting rotation of unrelated buildables (#24). No release that names 1.0. Client-only. |
| **Venture Farm Grid** (remake of Sarcen's FarmGrid) | place one crop, pivot to a second point to fix the field's orientation, draws a grid overlay | client-only, local config, `PlantSpacing` on top of the plant's own requirement | Cheap preview (one overlay, not N ghosts) but it needs a two-click mode and it cannot show per-cell validity. Config is local-only, so a group drifts. |
| **Farming** (Smoothbrain) | a Farming *skill*: faster growth, more yield, **-1% cultivator stamina per level**, multi-plant at 20+, no biome limit at 50+ | skill framework, server-installable to enforce config | The only mod that does the stamina ask, and it does it by level, not by config. Its multi-plant overlaps ours directly. |
| **Automatics** (eideehi) / **CropReplant** (Ivan0xFF) | harvest in a radius and replant from inventory or a designated chest | radius sweeps on a timer | Auto-replant prior art. Automatics is the richer one (seed reserves, container roles); CropReplant is the one-liner version. |

What nobody does: all four asks in one place, tied to world progress, with the numbers
**server-synced** so a group plays the same game. That is the gap and it is exactly NVLB's shape.

## 3. The module

One module, `Farming`, section `[Farming]`, `Theme = "Building & gathering"`, `Side = Client`
(flipped to `Both` while `[Farming] SelfTest = true`, the FastMining / SettlementDiscount trick).
Four sub-features behind their own toggles, the CorpseRunPlus pattern, because they share one piece
of plumbing (the cultivator's piece table, the per-crop pitch, the tier table).

### 3.1 Spacing and snap (tier 0, always on)

Pitch per crop, read live from the prefab so PlantEverything's crops and any other mod's crops work:

```
pitch(crop) = max(crop.m_growRadius, neighbour.m_growRadius) * SpacingFactor + ExtraSpacingMetres
```

`SpacingFactor` defaults to 2.0, which is MassFarming's rule and the one that provably satisfies
`HaveGrowSpace`: the check is a sphere of radius `m_growRadius` around each plant's own centre, so
a neighbour's collider has to sit outside that sphere. At `2 * growRadius` the neighbour centre is
one full radius clear, which covers every vanilla sapling's collider extent. The self-test prints
the margin per crop so a modded crop with a fat collider is visible before a player finds it.

Snap: if a healthy `Plant` of the same prefab is within `SnapSearchRadius` (4 m) of the ghost,
project the ghost position onto **that plant's** lattice (origin at its position, axes from its
yaw, rounded to the nearest multiple of `pitch`) and write the result back to
`m_placementGhost.transform.position` in a postfix on `UpdatePlacementGhost`. No mode, no second
click, no stored state: a field grows out of its own first plant, and a row extends straight. This
is deliberately not Venture's pivot mode, which needs a mode and a second click to say the same
thing that the neighbour's own rotation already says.

### 3.2 Cultivator stamina

Two patches, both gated on "the local player is in place mode holding a tool listed in
`[Farming] StaminaTools`" (default `Cultivator`):

* postfix `Player.GetBuildStamina()` and multiply by `StaminaMultiplier` (default 0.5). That is the
  real spend, both call sites.
* prefix `Player.HaveStamina(float)` and scale the requested amount the same way. Without it the
  gate at `Player.cs:1382` still demands the full vanilla `m_attack.m_attackStamina` and a tired
  player is refused a plant they can afford. The prefix is narrow: place mode, listed tool, nothing
  else, so no other stamina check in the game moves.

`m_attack.m_attackStamina` is never written. It lives on the shared prefab, the same argument that
keeps `WorkbenchReach` off `m_rangeBuild`.

### 3.3 Multi-plant, N x N, from the world tier

`[Farming] GridByTier` is a `[Tiers]`-shaped string, server-synced, hot-reloadable, with a picker:

```
GridByTier = "0:1,1:2,2:3,3:4,4:5,5:6,6:6,7:6"
```

Tier 0 is vanilla. Eikthyr (bronze age) 2x2, the Elder (iron) 3x3, Bonemass (silver) 4x4, Moder
(black metal) 5x5, Yagluth 6x6. `MaxGridCells` (default 36) is a second, hard cap so an admin can
hand out tiers without handing out a 6x6 of onions. `N` comes from `Frontier.WorldTier`, which is
read from replicated global keys and is identical on every client by construction, the same
argument `WorkbenchReach` makes for its range formula.

This is the Reddit comment's own "simpler way", not the iron/silver cultivator items. New items mean
new recipes, new assets, a new version lock, and a second progression axis next to the one NVLB
already has. The boss keys are the mechanic he noticed we already have.

Keys, through `NvlbKeys.Declare` so they are real rebindable `ZInput` buttons on Valheim's own
Keyboard & Mouse page:

* `FarmSingle` (default `LeftShift`, held): plant one, not the grid. The tier reward is on by
  default and the modifier is the escape hatch, not the other way round: a player who earned 4x4
  should not have to hold a key to use it.
* `FarmSnapToggle` (default `None`): turn snapping off for a freehand plant.
* `FarmGridDown` / `FarmGridUp` (default `None`): step N down or up within the tier's allowance, for
  someone who wants 2x2 in a tight corner.

The same `FarmSingle` key doubles as the harvest modifier (3.4). Different context, never ambiguous:
one is in place mode, one is hovering a `Pickable`.

Placement, following MassFarming's proven shape but on NVLB's terms:

* postfix `Player.UpdatePlacementGhost(bool)`: compute the N x N cell offsets in the ghost's own
  rotation frame, raycast each cell's Y to terrain, move the pooled ghosts, validate each cell.
* prefix + postfix on `Player.TryPlacePiece(Piece)`: vanilla places the anchor cell and its caller
  charges for it. The postfix then calls
  `PlacePiece(piece, cell.pos, cell.rot, doAttack: false)` for each remaining **valid** cell and
  charges exactly for those: `ConsumeResources(piece.m_resources, 0, -1, placedExtras)` plus
  `UseStamina(GetBuildStamina() * placedExtras)` when `StaminaMode = Linear` (default) or nothing
  extra when `Flat`. `doAttack: false` on the extras so the swing animation fires once, not 36
  times.
* postfix `Player.SetupPlacementGhost()`: rebuild or drop the ghost pool when the selection changes.

**Invalid cells are skipped, not fatal.** A grid that straddles a rock, a path or the edge of the
cultivated patch places the cells that pass and leaves the rest, and the player already saw which
ones in red. Failing the whole grid would make the feature unusable at exactly the moment it is
most wanted, which is the "grid placement not working" class of MassFarming issue. The cost follows
the cells actually placed, never the cells asked for.

**Cell validity is vanilla's own check, per cell**, not a reimplementation: each pooled ghost is a
real instance of the piece prefab, so `Plant.HaveGrowSpace`, `m_needCultivatedGround`, the biome
test and `Piece.m_cultivatedGroundOnly` all answer for themselves. Two things are deliberately not
checked per cell: the player's `m_maxPlaceDistance` (a 6x6 necessarily reaches past it, and the
anchor cell is always the one the player really aimed at) and `PrivateArea` (vanilla's own check
runs inside the real place call and will refuse the cell there).

**Ghost preview: N x N real ghosts, pooled.** Not a single ghost with a footprint outline. Vanilla's
entire placement vocabulary is the ghost itself, tinted by
`Piece.SetInvalidPlacementHeightlight`, and a player reads a grid of green-and-one-red instantly
without being taught anything. An outline is a new visual language and, worse, it cannot say
*which* cell is blocked, which is the single fact the player most needs. The cost is real (this is
what forced PlantEasily's 56x ghost-update rewrite) and is paid for by construction: the pool is
built once per (prefab, N) and reused, per-frame work is transform writes only, and the validity
sweep is throttled to `GhostRevalidateHz` (default 10) with the last verdict cached and reused at
place time.

### 3.4 Mass harvest and auto-replant

Hold `FarmSingle` and Use on a `Pickable`: pick every `Pickable` of the **same prefab** within
`HarvestRadius`, capped at `HarvestMaxItems` (default 25) so one keypress cannot fire an unbounded
RPC burst at the server. `HarvestRadius` defaults to 0, meaning "auto": the footprint radius of the
current tier's grid for that crop, `pitch * (N - 1) / 2`. A planted patch has no identity in the
save (there is no group ZDO), so "harvest the grid I planted" is not recoverable; a radius that
matches the grid you would have planted is the honest version of that wish, and it stays honest as
the tier grows.

Each pick is vanilla's own `Pickable.Interact(player, false, false)`, so the skill roll, the bonus
yield, the stats and the RPC are all vanilla's.

Auto-replant (`ReplantOnHarvest`, default true): after a pick, if the picked prefab maps back to a
plantable piece and the seed is in the inventory, place that piece at the picked position and
charge for it exactly. The map is **auto-discovered** at world load, the BuildersGuild
structural-set trick: walk the cultivator's `PieceTable`, and for each piece with a `Plant`, follow
`Plant.m_grownPrefabs` to the grown prefab, read its `Pickable.m_itemPrefab`, and key on that.
`ReplantMap` overrides it (`name=piece` adds, `-name` removes) and the self-test dumps the derived
table. `ReplantReserve` (default 0) keeps N seeds back so a player is never left with none.

### 3.5 What stays authoritative

NVLB version-locks, so everyone on the server has the mod and the same synced numbers. Beyond that,
honestly: vanilla runs placement, `ConsumeResources` and `UseStamina` on the placing client, so
there is no server ledger to appeal to, and a `Plant` created without payment is byte-identical to
one that was paid for. What we can guarantee, and do:

* **N is not a client opinion.** It comes from server-synced `GridByTier` and from global keys the
  server pushed. A client cannot widen its own grid by editing a local file, because `BindSynced`
  overwrites it.
* **The seed cost is exact and is taken through vanilla's own path.** One `ConsumeResources` call
  with `multiplier = placedExtras`, counted from cells that really got a `PlacePiece`, never from
  cells requested. If `HaveRequirements` fails for the full grid we shrink the grid to what the
  inventory can pay for rather than placing free plants.
* **Nothing new is written to a ZDO.** No custom RPC, no new save state, so an NVLB world opens in
  vanilla unchanged.

### 3.6 Other mods

`Coexist` (`Auto` / `Always` / `Never`, default `Auto`). At `Auto`, resolve installed plugins
through `BepInEx.Bootstrap.Chainloader.PluginInfos` by GUID (the `SmoothServerBridge` pattern, no
compile-time reference) and **stand down** the overlapping sub-features, logging one line:

* PlantEasily, MassFarming, Smoothbrain Farming: all three patch the same
  `UpdatePlacementGhost` / `TryPlacePiece` chain. Two grids fighting over one ghost is the
  "cannot use hammer or hoe" class of bug. Turn off grid, snap and harvest; **keep the stamina
  multiplier**, which none of them contest except Smoothbrain Farming (turn it off for that one too).
* Venture Farm Grid: turn off snap only; the grid and harvest do not collide with it.
* PlantEverything: coexist, always. It adds crops; we read their `m_growRadius` live, so its crops
  get spacing and grids for free.

GUIDs to be confirmed against the shipped DLLs before this is written, not from memory.

NVLB's own neighbours: `WorkbenchReach` does nothing here, because the cultivator requires no
crafting station, and this module deliberately does not add a "cultivator reach" knob; the anchor
cell stays at vanilla place distance. `BuildersGuild` prices build pieces through
`TrailingTierDiscount`'s requirement context, and a sapling's seed cost goes through
`Piece.Requirement.GetAmount` like everything else, so a crop would pick up a yard discount if a
seed were ever listed in `[Builders] Materials`. It is not, and should not be; note it in
`MODULES.md` so nobody adds "Carrot:0.5" without meaning to.

## 4. Settings

```ini
[Farming]
Enabled            = true
SnapEnabled        = true     # tier-0 QoL
SpacingFactor      = 2.0      # x the crop's own m_growRadius
ExtraSpacingMetres = 0.0
SnapSearchRadius   = 4.0      # m, to find the neighbour that defines the lattice
StaminaMultiplier  = 0.5      # SIMPLE, Gathering: "How tiring the cultivator is"
StaminaTools       = Cultivator          # item prefab names, PickerSource.Items
GridEnabled        = true
GridByTier         = 0:1,1:2,2:3,3:4,4:5,5:6,6:6,7:6
                              # SIMPLE, Gathering: "How big a patch you can plant at once"
MaxGridCells       = 36       # hard cap over the table
StaminaMode        = Linear   # Linear | Flat
GhostRevalidateHz  = 10
HarvestEnabled     = true
HarvestRadius      = 0        # 0 = auto, match the current grid's footprint
HarvestMaxItems    = 25
ReplantOnHarvest   = true     # SIMPLE, Gathering
ReplantReserve     = 0        # seeds kept back
ReplantMap         =          # overrides the auto-derived map
Coexist            = Auto     # Auto | Always | Never
# local (never synced)
FarmSingleKey      = LeftShift            # also the harvest modifier
FarmSnapToggleKey  = None
FarmGridDownKey    = None
FarmGridUpKey      = None
SelfTest           = false    # flips Side to Both
```

Simple view gets four rows only: cultivator stamina, patch size, replant on harvest, and the module
toggle. Everything else is Advanced.

## 5. Headless self-test (`[Farming] SelfTest = true`)

1. Cultivator piece table resolved; every piece with a `Plant` listed with its `m_growRadius`,
   `m_needCultivatedGround` and biome mask.
2. Pitch table per crop, with the margin `pitch - growRadius` proved positive, and any crop whose
   collider extent eats the margin named.
3. `GridByTier` parses; N printed for tiers 0..7, clamped by `MaxGridCells`.
4. Cell-offset generator for N = 1..6: count is N*N, offsets are unique, symmetric about the
   anchor, and the anchor is cell centre.
5. Seed arithmetic: for k placed cells, extras = k-1, and `1 + (k-1)` equals k for k = 1..36.
6. Stamina arithmetic: `Linear` vs `Flat` for k cells at multiplier m, and the `HaveStamina` prefix
   scales by exactly the same m.
7. Replant map auto-derived: piece to grown prefab to `Pickable.m_itemPrefab`, printed in full, with
   unresolved entries named.
8. Stand-down: with a fake plugin GUID injected, grid / snap / harvest report disabled and the
   stamina multiplier reports still on.

## 6. Work breakdown

| File | Lines | Contents |
| --- | --- | --- |
| `src/Modules/Farming/FarmingModule.cs` | ~420 | config, tier table, discovery, `StatusDetail`, stand-down, hot reload |
| `src/Modules/Farming/FarmGrid.cs` | ~260 | pitch, lattice, snap, cell offsets, per-cell verdict cache |
| `src/Modules/Farming/FarmGhosts.cs` | ~220 | pooled ghost instances, throttled revalidation, highlight |
| `src/Modules/Farming/FarmPlant.cs` | ~200 | `TryPlacePiece` pre/post, extra `PlacePiece` loop, exact consume and stamina |
| `src/Modules/Farming/FarmHarvest.cs` | ~200 | modifier + Use radius pick, auto-replant, reserve |
| `src/Modules/Farming/FarmingSelfTest.cs` | ~180 | the eight checks above |
| `docs/MODULES.md` | +1 row | |
| `README.md` | module count | 41 to 42 |

About 1,500 new lines. Stage it: 3.1 + 3.2 first (the two tier-0 QoL asks, shippable alone and
low risk), then 3.3, then 3.4.

## 7. In-game test script for Matt

1. NEWTEST, `[Frontier] TierOverride = 0`. Cultivate a patch, plant carrots. Stamina bar should
   drain about half as fast as you remember. One plant per click.
2. Plant a second carrot near the first: the ghost should jump onto the first one's row and sit at a
   fixed distance. Walk round it and try to place at an angle: it stays on the lattice.
3. Hold the snap-off key and place freehand: the ghost stops snapping at once.
4. Let both grow. Neither should ever show "it has no space" on hover. That is the spacing proof.
5. `TierOverride = 1`. Same patch: the ghost becomes a 2x2 of ghosts. Place it. Four seeds leave the
   bag, no more, no fewer. Check the bag count before and after.
6. Aim the 2x2 so one cell is over a rock or uncultivated dirt. That cell turns red. Place: three
   carrots appear, three seeds leave the bag.
7. Hold the single key: the grid collapses to one ghost. Release: it returns.
8. `TierOverride = 3` for 4x4. Place on a big cultivated field. Sixteen seeds, sixteen carrots, one
   swing animation. Then repeat with only 5 seeds in the bag: the grid should shrink to 5 cells
   rather than placing 16 free carrots.
9. Grown carrots: hold the modifier and Use one. Everything in the patch gets picked in one press
   and, if seeds are in the bag, replanted where it stood.
10. `nvlb.status` should name the module, the current N, the pitch for the selected crop, and the
    stand-down state. Then `cfg.py` `StaminaMultiplier = 1.0` live: the very next plant costs
    vanilla stamina with no restart.

## 8. Open decisions

1. **Boss keys only, or an iron/silver cultivator too?** Recommendation: boss keys only for v1, per
   the commenter's own "simpler way". Tool tiers are a second progression axis and a lot of asset
   and recipe work for the same feeling.
2. **Grid on by default, single on a held key (recommended) or the reverse?** The reverse is what
   PlantEasily and MassFarming do, and it is what a returning player from those mods expects.
3. **Harvest radius auto-matched to the current grid (recommended) or a plain metres setting?** Auto
   grows with the tier and needs no explaining; a fixed number is easier to reason about and to
   answer questions about in chat.
4. **Stand down silently when PlantEasily / MassFarming is present (recommended), or just warn?**
   Standing down means a player with both installed sees our stamina rule and their grid. Warning
   only means two mods fight over one ghost and the bug report lands on us.
