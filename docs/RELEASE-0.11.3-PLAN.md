# NoVikingLeftBehind 0.11.3 - release plan (written 2026-09-18, after the r/valheim launch post)

Matt: "11.3 will be a bigger update probably. No rush to ship it, we can make sure it's all perfect."
Everything below was planned from the launch-post comments and GitHub issue #10; the detailed design docs
are linked per item. Nothing here is shipped yet. Status column reflects 2026-09-18 ~14:00 UTC.

> **2026-09-21 re-scope (Matt: "prioritize the real fixes first. Once that's shipped we'll release the extra stuff").**
> **0.11.3 SHIPPED 2026-09-22 04:20 UTC (bug fix only):** issues #11 + #12 (Haldor's purchasable inventory rows vs the 4-row extra-slot layout), branch `fix/haldor-bag-rows`,
> worktree `scratchpad/nvlb-bagrows`, lab `NVLB_bagrows`. Needs Matt's in-game test (it moves items between cells) before shipping.
> **SmoothServer 0.6.1 SHIPPED 2026-09-22 = issue #3** (SharedMap receives nothing behind several ServerSync mods), branch `fix/sharedmap-wrapped-socket`, lab `SS_sharedmap`.
> **Everything in the table below moves to the release after (0.12.0):** the four built branches, then clock / farming / docks once decided.
> Hit-delay result for the record: cross-owner hit p50 160-200 ms (17 Sep) -> 10-50 ms over 87 sampled minutes (18-21 Sep), late=0.

## Scope

| # | Feature | Source | Design doc | Status |
|---|---------|--------|------------|--------|
| 1 | Quick-slot HUD row under the hotbar (item + key, bag closed) | GitHub #10 (Bigshot0910) | `docs/QUICKSLOT-HUD-PLAN.md` | BUILT (04ee6cc), audit CLEAN, awaiting Matt's in-game test - branch `feat/quickslot-hud`, worktree `scratchpad/nvlb-quickhud`, lab `NVLB_quickhud` |
| 2 | Ammo hotkeys (Alt+1..N) shown on the AmmoHud tiles | Matt (the mod the ammo slots came from had them) | in the agent's report / CHANGELOG entry | BUILT (bf7d554), audit CLEAN, awaiting Matt's in-game test - branch `feat/ammo-hotkeys` (`[AmmoHotkeys]` Modifier=LeftAlt + Slot1-4Key, labels on the tiles, vanilla hotbar suppressed only for Alt+<bound key>), worktree `scratchpad/nvlb-ammokeys`, lab `NVLB_ammokeys` |
| 3 | Farming: grid snap + spacing, cultivator stamina, NxN mass plant by tier, radius harvest + replant | Reddit (ssparda, ThatDude1115) | `docs/FARMING-PLAN.md` (338 lines) | DESIGNED - needs Matt's 4 decisions, then build (~1,500 lines, 6 files) |
| 4 | Clock: text-only "Day 42 - Morning" under the minimap | Reddit (ssparda) | `docs/CLOCK-PLAN.md` | DESIGNED - needs Matt's 3 decisions, then build (small) |
| 5 | Admin travel commands `nvlb.findloc` / `nvlb.goto` (+ ServerSync admin-flag fix) | 1.0.14 disables cheat commands for dedicated-server clients | header comment in `src/Modules/Debug/AdminTravelModule.cs` | BUILT, UNTESTED in game - branch `feat/admin-goto` (d9026e4), rebase onto main |
| 6 | Partial ore-node regrowth | agreed 2026-09-17 | `[Regrowth] PartialEnabled / PartialMinRemovedFraction` | BUILT, UNTESTED in game - branch `feat/regrow-partial` (b0b381e) |
| 7 | README line: `[Access] EnforceClientMod=false` = vanilla/console players can join but get no NVLB features | crossplay/PS5 Reddit questions | - | TODO (one line) |
| 9 | Docks: waterproof wood as a tiered water-only wear floor (no resin step) + dock-building help (hammer while swimming, readable ghost under water, seabed snap, auto-extend supports to the floor, charged honestly) | Matt (2026-09-18, boat-dock frustration) | `docs/DOCKS-PLAN.md` (384 lines) | DESIGNED - two modules `[Waterproof]` (tiered water-only floor) + `[Docks]` (reach bonus under water = the real fix, hammer while swimming w/ stamina reserve, submerged ghost tint, auto-extend poles); needs Matt's 4 decisions (tiered floor / auto-extend default on / stairs out of v1 / reach over water only) |
| 8 | Group profile: swap deprecated `Azumatt-Official_BepInEx_ConfigurationManager` for `shudnal-ConfigurationManager` | r2modman shows it deprecated | `/opt/modlab/tools/cutover/profile-code.py` MODS list | TODO (not an NVLB change; do at the next code mint) |

Decisions already taken (Matt, 2026-09-18) for item 1: quick slots stay raw KeyCodes for now (promoting them to
real Valheim keybindings is a later change); hide the row on gamepad; always one row; no hover tooltip.

## Clock - the idea (from CLOCK-PLAN.md)

Text only, nothing painted. Clones one of the game's own text elements, so it is the Norse font and colour the HUD
already uses, and sits directly under the minimap, measured from the minimap's real position. Default reads
`Day 42 - Morning`; optional `14:45` (24h, from `EnvMan.GetDayFraction()`), optional real-world time, both off by
default. Fades in over a second, updates once per game minute, hides whenever the HUD / map / cutscene hides the
minimap. The period word is vanilla's own judgement: the environment code exposes exactly three tests (night, day,
afternoon) at quarter-day boundaries; vanilla itself has no persistent clock, only the "Day N" message on waking.
Prior art: aedenthorn ClockMod (deprecated, custom look, no 1.0 confirmation), orfox ValheimClock (painted gold-trim
panel with themes). Nothing text-only that reuses vanilla widgets exists.

Decisions for Matt (recommendation first):
1. Second half of the day is called "Evening" (reads better) rather than the code's "Afternoon".
2. Add a "HUD & display" group to the Simple view for Enabled + Format; it can also hold the QuickSlotHud and
   AmmoHud toggles.
3. Enabled by default. First always-on visual NVLB adds, so it is Matt's call; the commenter's point was "out of
   the box", and it is one click to turn off.

## Farming - the idea (from FARMING-PLAN.md)

One `Farming` module (`[Farming]`, client side), four sub-toggles on shared plumbing.

In play: with the cultivator, at tier 0 and before any boss, (a) the ghost snaps into line with the nearest row you
already planted (lattice of the nearest same-crop plant within 4 m), (b) plants are spaced by their real
`Plant.m_growRadius` x 2.0, which provably satisfies vanilla's `HaveGrowSpace`, (c) cultivating costs half the
stamina (`Player.GetBuildStamina` postfix + a narrow `HaveStamina` prefix so the gate agrees). Then the grid grows
with the world tier the NVLB way: `GridByTier = "0:1,1:2,2:3,3:4,4:5,5:6,6:6,7:6"` (a `[Tiers]`-shaped synced
string) plus a `MaxGridCells` cap; N comes from `Frontier.WorldTier`, identical on every client by construction.
Preview = N x N real pooled ghosts, each tinted exactly like vanilla's single ghost, so blocked cells are visible;
blocked cells are skipped, not fatal. Seeds are consumed only for cells that actually planted, through vanilla's own
`ConsumeResources`; if the bag cannot pay the whole grid, N shrinks rather than planting for free. Harvest: hold a
key + interact picks every same-crop Pickable within a radius matched to the current tier's grid (cap 25), each via
vanilla `Pickable.Interact` so skills/stats stay vanilla; optional auto-replant from seeds in the bag, map
auto-discovered from the cultivator piece table. Keys via `NvlbKeys.Declare` (real rebindable buttons).
Authority: N and every number are `BindSynced`; no new ZDO, no custom RPC, the world still opens in vanilla.
Coexistence: stand down grid/snap/harvest if PlantEasily or MassFarming is present, everything for Smoothbrain
Farming, snap only for Venture Farm Grid; always coexist with PlantEverything (inherits its crops).

Prior art in one line each: PlantEverything (crops, coexist) / PlantEasily (best UX, client-only, "do not install on
a dedicated server", settings not fully synced) / MassFarming (the mechanical blueprint, 12 open issues, no 1.0
release) / Venture Farm Grid (overlay preview, two-click pivot, local config) / Smoothbrain Farming (the only
stamina reducer, collides on multi-plant) / Automatics + CropReplant (replant prior art). Nobody ties the grid to
progression.

Decisions for Matt (recommendation first):
1. Grid growth from boss keys only, no iron/silver cultivator items.
2. Claude disagrees with the plan's author here: ship vanilla single-plant by default and HOLD a key for the grid
   (the plan recommends the reverse). Accidentally spending 25 seeds when you meant one gets a mod uninstalled, and
   hold-for-grid is what PlantEasily / MassFarming players already expect.
3. Harvest radius auto-matched to the tier's grid, with a metres override.
4. Stand down silently (one log line) when a conflicting farming mod is present.

Size: ~1,500 lines / 6 files, staged QoL-first (snap + spacing + stamina, then the grid, then harvest/replant).
The biggest piece of 0.11.3 and the one that most needs Matt's in-game pass.

## Ship sequence (when everything is in)

Merge order: feat/admin-goto (rebase) -> feat/regrow-partial -> feat/quickslot-hud -> feat/ammo-hotkeys ->
clock -> docks -> farming; each with its own in-game test by Matt (client-side features cannot be proven headless).
Then bump 0.11.3 (Plugin.cs / csproj / manifest <= 256 chars / CHANGELOG date / README build line + module and
settings counts), lab build v1_0_14, cecil-audit, NEWTEST boot, merge main with `-F <file>`, tag, push main + tag,
CI, dist zip, `newworld.sh deploy-nvlb <dll>` when empty (version lock: friends update first), mint code
(pin NVLB 0.11.3 + SS current), VALHEIM-CONNECT.md, close #10 with a note, Reddit reply to the two commenters.
