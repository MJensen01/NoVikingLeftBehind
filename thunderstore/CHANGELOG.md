# Changelog — NoVikingLeftBehind

## 0.7.1 (2026-09-08)
**A Network panel in the settings tab.** If your group also runs [SmoothServer](https://thunderstore.io/c/valheim/p/Nosferatu/SmoothServer/),
the NoVikingLeftBehind tab now has one more entry — **Network (SmoothServer)** — with the handful of its switches
that actually fix a bad afternoon. It appears only when SmoothServer is installed, and it is deliberately five rows
and not its sixty knobs: the rest stay in its config file, where they belong.

- **The five rows.** *Network preset* (`[Profiles] Profile` — `Default` / `FastLink` / `Custom`), *Compression*
  (`[Compression] Enabled`), *Shared map* (`[Map] Enabled`), *Smooth motion (this PC)* (`[SmoothMotion] Enabled`,
  written to your own machine's config and nobody else's), and the admin-only *Require the client mod*
  (`[General] EnforceClientMod`). Each has the same one-line hint under its name and SmoothServer's own full
  description on hover. A **Reset network to Default** button under them puts the preset back to `Default` with
  compression and the shared map on, after asking you to confirm.
- **The door writes a second config file.** A change to one of these goes through exactly the machinery every other
  setting does — permission, tier, value validation, the rate limit, a timestamped `.bak-` backup, the undo history,
  the chat line and the server log — and then writes `Nosferatu.SmoothServer.cfg` in the same directory. SmoothServer's
  own file watcher and its own ServerSync take it from there, so a preset flip re-tunes the whole server live and
  everyone's client follows. The audit line names the switch the way the menu does: `[NVLB] Erik set Network preset
  FastLink -> Default`.
- **Five keys, by name, and nothing else.** The server validates every incoming SmoothServer change against a
  hand-written allowlist (section, key, type, permitted values, tier). Anything not on it is refused however it is
  asked for, including by an admin — the panel can never become a remote control for that mod's other settings. If
  SmoothServer is not installed on the server, the door answers "SmoothServer is not installed on this server".
- **No dependency either way.** There is no compile-time reference to SmoothServer: it is found at runtime through
  BepInEx's plugin list and read through its own live config entries. Without it this release behaves exactly like
  0.7.0. SmoothServer itself is unchanged — nothing in it is patched, wrapped or reimplemented.
- **Refusals that tell you the truth.** SmoothServer's preset re-asserts its own table over the config file after
  every reload, so switching compression off while the preset owns it would be silently undone a second later. The
  door refuses that up front and says to set the preset to `Custom` first. A SmoothServer module that booted disabled
  is answered with "Needs a server restart", read from its own module list.
- **New: `[Access] NetworkSelfTest`** (admin, off by default). On a dedicated server it drives the whole thing once at
  world load — flip the preset and back, re-read SmoothServer's file from disk each time, undo both, round-trip a key
  in a second section, and prove the three refusals — then restores everything. 235 settings -> 236; still 35 modules.

## 0.7.0 (2026-09-08)
**The settings menu** — a NoVikingLeftBehind tab inside Valheim's own Settings screen, usable by everyone on the
server, that changes any hot-reloadable setting live. Until now only whoever had a shell on the server box could
change anything, which is not much use to friends playing while the owner is at work. 33 modules -> **35 modules**;
every one of the **235 settings** now describes itself. Nothing about the config file changes: `cfg.py`, the
timestamped backups and hand edits all keep working exactly as before.

- **Every setting declares itself** (`src/Config/Opt.cs`, `src/Config/ConfigCatalog.cs`). The two bind helpers
  every module already goes through, `BindSynced` / `BindLocal`, take an optional `Opt` carrying a **hint**
  (<= 12 words, plain language, what it affects), a **range** (min/max/step) for numbers, **choices** for pick-one
  strings, a **tier** (who may change it) and **live** (false where a value is only read once at boot).
  `FeatureModule` gained `Theme` and `Hint` so each module has a group and a one-liner too. The result is a
  `ConfigCatalog` that the settings tab, the server's validation and the new `nvlb.catalog` console command all
  read, so the three can never disagree. Counts on a fresh server: 235 settings, 183 synced / 52 local, 193
  Everyone / 42 admin-only, 224 live / 11 restart-only.
  Ranges are deliberately **not** handed to BepInEx as `AcceptableValueRange`: BepInEx clamps to an acceptable
  range when it loads a file, which would silently rewrite a server's existing cfg the first time a range here
  turned out to be too tight. They are advisory for the UI and enforced only at the door, where a refusal is
  visible and reversible.
- **Access — new module** (`src/Access/AccessModule.cs`, `[Access]`, Side `Both`). One door, on the server, that
  every change goes through. A client sends `NVLB_Tweak(section, key, value)` over `ZRoutedRpc`; the server checks
  `[Access] TweakAccess` (`Everyone` by default), the per-setting tier against `adminlist.txt`, that the setting is
  known and live, parses and validates the value against the catalog, rate-limits the sender
  (`MaxChangesPer10s=10`), and then **writes the cfg file on disk** with a timestamped `.bak-` backup in exactly
  the format `cfg.py` uses — so the existing file watcher, `Config.Reload()` and ServerSync push it to everyone
  through the path that already existed. The cfg file stays the one source of truth; the mod does not grow a
  second config system. Every change is announced once to the whole server (`Announce=Chat` by default,
  `Message` / `Both` / `Off` available) and written to the server log, and the server keeps a 20-deep-per-key undo
  history behind `NVLB_Undo`. `NVLB_ResetModule` puts a whole section back to its defaults under a single backup.
  Local (per-player) settings never reach the server at all — the tab writes the client's own cfg.
  `[Access] SelfTest=true` makes a dedicated server drive the entire door once at world load — change a setting,
  re-read the file from disk to prove it changed, hold for 45 seconds, undo, re-read again — and log every step.
  Admin-only by default: `[ServerKeys]` (every key), `[General] EnforceClientMod` / `Mode` / `HotReload`,
  `[Debug] AllowTestCommands`, every `SelfTest` / `DryRun`, and `[Access]` itself. `[Frontier] TierOverride` and
  `[Tiers] MaterialTiers` are deliberately left open to everyone — players may move their own frontier.
- **SettingsMenu — new module** (`src/Ui/SettingsMenuModule.cs`, `[SettingsMenu]`, Side `Client`). A
  **NoVikingLeftBehind** tab in Valheim's Settings screen, from the pause menu in game and from the main menu.
  Left column: every module grouped by theme, each with its `Enabled` toggle inline. Right column: the selected
  module's settings, one row each — label, the hint underneath, a control (toggle / slider with a typed number
  box / pick-one / text field), a reset-to-default button, and the full description on hover. A search box at the
  top filters label, key, hint and description across all 235 settings. The header says who may change things and
  shows the last five changes anyone made; the footer has Undo and Reset-module. Values move under you when
  somebody else changes something, because the row redraws on `SettingChanged` rather than on a click. A row you
  may not change is greyed with the reason — "Needs a server restart", "Only a server admin can change this",
  "Join a server to change this" — rather than hidden, so everyone can see what the mod can do
  (`[SettingsMenu] ShowUnavailable=false` hides them instead).
  The hook is a **prefix** on `Settings.Awake`, not a postfix: `Awake` calls `InitializeTabs()`, which snapshots
  the tab list into a private list it later indexes by tab number, so a tab added afterwards would index out of
  range. Added first, with a component implementing `Valheim.SettingsGui.ISettingsTab` on its page, vanilla drives
  the tab exactly as it drives Gameplay or Audio.
  **No art is shipped.** Every control is a runtime clone of a vanilla one found on the other settings pages, so
  the tab inherits the game's fonts, colours, parchment and UI scale — and keeps inheriting them through a
  reskin. Clones have their prefab-authored UnityEvents replaced and their page-bound tooltips stripped, because
  `Instantiate` keeps persistent listeners whose targets live outside the copied subtree.
- **`nvlb.catalog`** — new console command next to `nvlb.status`: every setting with its hint, type, range,
  permission tier and whether it applies live, optionally filtered by text. A dedicated server, which has no
  terminal, writes the same content to `nvlb-catalog.tsv` beside its cfg at boot.

## 0.6.0 (2026-09-08)
**Building & gathering** — four new modules for the half of Valheim that is a construction game: tools that tear
through material they outclass, a workbench whose reach grows with your settlement, cheaper building as the clan
grows, and half-weight materials next to the bench. 29 modules -> **33 modules**.

- **OverkillTools — new module** (`src/Modules/Building/OverkillToolsModule.cs`, `[Tools]`, Side `Both`).
  A hit on a tree, log, stump, rock or plain destructible is scaled by
  `min(1 + PerTierBonus x (hit.m_toolTier - target.m_minToolTier), MaxMultiplier)` — a black metal axe fells a birch in
  a couple of swings, an iron pickaxe bites a boulder harder than an antler one, and **day-one Meadows is untouched**,
  because a stone axe on a beech has a tier gap of 0 and gets exactly vanilla numbers. Defaults `PerTierBonus=0.5`,
  `MaxMultiplier=3.0`, `AffectTrees/AffectRocks/AffectDestructibles=true`.
  Hooks are the five owner-side handlers that already gate on tool tier — `TreeBase.RPC_Damage`, `TreeLog.RPC_Damage`,
  `MineRock.RPC_Hit`, `MineRock5.RPC_Damage`, `Destructible.RPC_Damage` — and the whole hit is scaled with
  `HitData.DamageTypes.Modify(float)`, so a chop hit scales its chop and a pickaxe hit its pickaxe with no per-damage-
  type special cases. **Deterministic by construction:** the formula reads only the target prefab's own
  `m_minToolTier` and the hit's serialised `m_toolTier`, so it is identical whichever machine owns the object — which
  is why the module is `Both` and its patch bodies deliberately do *not* gate on the running side.
  A bonus needs a player's tool: `HitType.Structural` is rejected outright (`MineRock5.CheckSupport` manufactures a
  `m_toolTier=100` hit to collapse unsupported areas), a `Player` attacker always qualifies, any other `Character`
  never does, and an attacker whose object is not instantiated on this machine qualifies only when `m_toolTier > 0`.
  **No double-dipping with FastMining:** every prefab in `[Mining] OreNodes` — fractured stages included — is skipped,
  whether or not FastMining is enabled, so copper, tin, silver, obsidian and meteorite behave exactly as they did in
  0.5.1. Creatures, players and `WearNTear` build pieces are never patched at all.
  `[Tools] SelfTest=true` logs a table of 26 real prefabs with their component family and `m_minToolTier` and the
  multiplier at tool tiers 0–4.
- **WorkbenchReach — new module** (`src/Modules/Building/WorkbenchReachModule.cs`, `[Workbench]`, Side `Both`).
  `effective range = vanilla + PerTierMetres x world tier + PerLevelMetres x (level-1)`, hard-capped and never below
  vanilla. Defaults `PerTierMetres=2`, `PerLevelMetres=6`, `MaxRangeMetres=60`, `Stations="piece_workbench"`. On the
  test world (tier 3) that is **26 m at level 1, 46 m at level 3 and 60 m (capped) at level 5**, against vanilla's 20 m.
  One postfix on `CraftingStation.GetStationBuildRange()` moves every range check together — placement
  (`Player.HaveRequirements(Piece, RequirementMode)`), deconstruct **and** hammer repair (`Player.CheckCanRemovePiece`),
  the build HUD's workbench row (`Hud.SetupPieceInfo`) and CraftFromChests' own build check all go through the same
  static — so the HUD, the validation and the consume path cannot disagree. `m_rangeBuild` lives on the shared prefab
  and is **never mutated**: only the return value is rewritten, per instance, per call. A postfix on
  `CraftingStation.ShowAreaMarker()` grows the hover circle to match, so you can see the reach you have.
  **The monster-spawn suppression area is deliberately NOT enlarged** — vanilla sizes that `EffectArea.PlayerBase`
  collider from the same field this module never writes, so it stays exactly vanilla.
- **SettlementDiscount — new module** (`src/Modules/Building/SettlementDiscountModule.cs`, `[Settlement]`, Side
  `Client`). Build pieces cost `max(1 - PerTierDiscount x world tier, 1 - MaxDiscount)` — 10% per boss down to a floor
  of half price, so a three-boss world pays x0.70 and a wood door costs 3 wood instead of 4. Defaults
  `PerTierDiscount=0.10`, `MaxDiscount=0.50`, `MinAmount=1`, plus optional `ExcludePieces` and `OnlyCategories`.
  **Build pieces only** — crafting recipes cost exactly what they always cost. The factor is composed into
  TrailingTierDiscount's existing requirement-scaling context (`tierMult x stationMult x settlementFactor`, rounded
  once), rather than added as a second `GetAmount` postfix that would round twice; it keeps its own section and its own
  `Enabled` so it can be toggled live and independently, but the shared machinery is installed by `[Discount]`, which
  must therefore be enabled at startup.
- **Deconstruct refunds can no longer exceed the price** (`Piece.DropResources(HitData)`, new prefix + finalizer in
  `TrailingTierDiscountModule`). Vanilla's refund path reads `requirement.m_amount` **directly**, never `GetAmount()`,
  so any build-piece discount used to let you build a wall cheap and break it for full price, over and over. Both the
  cost and the refund now go through one function — `ScaledAmount(amount, PieceCostFactor(piece))` — so they are equal
  by construction. This also closes the same hole for the tier discount, which has been open since 0.3.0. One
  documented consequence: a piece built before a discount applied refunds today's cheaper price, deliberately erring
  against the player.
- **BuildersLoad — new module** (`src/Modules/Building/BuildersLoadModule.cs`, `[Load]`, Side `Client`). Inside a
  bench's build range, listed building materials weigh `WeightMultiplier` (0.5) — 60 wood drops from 120 to 60, 60 iron
  from 720 to 360 — so a big wall is a couple of trips instead of ten. Defaults `Stations="piece_workbench,piece_stonecutter"`,
  `HysteresisSeconds=3`, `CheckIntervalSeconds=0.5`, and 19 materials, every one of them resolved against 0.221.13's
  ObjectDB before shipping. One postfix on `Inventory.GetTotalWeight()`, the single accessor every encumbrance and
  weight readout goes through, and it returns immediately unless the inventory *is* the local player's own — chests,
  carts, ships and other players are structurally out of reach. Range comes from WorkbenchReach's shared helper, so
  "near a bench" means exactly what the build hammer means by it. **Hysteresis** keeps you "in range" for 3 s after
  walking out, so the encumbrance arrow cannot flicker on the boundary. The carry-weight **cap** is never touched and
  nothing is stored: walk away and the weight comes straight back.
- **Docs.** New "Building" theme section in both READMEs with four `Tune it:` lines, four new rows in
  `docs/MODULES.md` (33 modules + `Tiers` = 34 rows), and the module count updated everywhere it is stated.

## 0.5.1 (2026-09-08)
**Per-station recipe costs** — TrailingTierDiscount can now re-price every recipe made at a named crafting station,
whatever its tier. Still 29 modules; no new module.

- **`[Discount] StationMultipliers = "BCA_CookingPot:0.75"` — new setting** (`src/Modules/Economy/TrailingTierDiscountModule.cs`).
  Comma-separated `StationPrefabName:multiplier` pairs. Every recipe whose `m_craftingStation` prefab name is listed
  costs that fraction of its ingredients at **every quality level** — in the crafting panel, in the "can I craft this?"
  check and in what actually leaves your inventory. Matching is on the **prefab** name
  (`recipe.m_craftingStation.gameObject.name`), never the localised `m_name`; recipes with no crafting station, and
  build pieces, are never matched. Multipliers are clamped to `0.01..1` (this module still never makes anything more
  expensive) and compose with the tier discount by multiplication, with `MinAmount` still the floor.
  **Why:** CookingAdditions rewrites its own "Crafting Costs" back to its defaults on every server boot, so its soups,
  salted meats and coated eggs cannot be re-priced from its config at all. The default entry takes that mod's custom
  cooking pot to three-quarter price; vanilla stations are untouched unless you list them.
- **The number shown, checked and consumed still cannot disagree.** The station factor rides the same thread-static
  context as the tier factor and is applied in the same one place (the `Piece.Requirement.GetAmount(int)` postfix), so
  nothing in `ObjectDB` is mutated — deliberately: another mod's ItemManager re-applies its own numbers over the shared
  recipe data, and editing `Recipe.m_resources` would be a fight you lose on the next boot. One new context site,
  `InventoryGui.DoCrafting(Player)`, publishes `m_craftRecipe`'s station around the craft, because the
  `Piece.Requirement[]` handed to `Player.ConsumeResources` cannot say which station it came from.
- **Proof in the log.** `[Discount] station multipliers: BCA_CookingPot x0.75 (10 recipes matched)` is written once per
  world load — from the `ZoneSystem.Start` hook, the first point `ObjectDB.m_recipes` is populated on **both** halves —
  and again on every live edit of the setting, on a dedicated server too (where this client-side module is otherwise
  `disabled(side)`). The match count is in `nvlb.status`, and `[Economy] SelfTest=true` now prints five matched recipes
  ingredient by ingredient with their before/after amounts.

## 0.5.0 (2026-09-08)
**Crew sailing** — two modules that make a boat with people on it feel different from a boat with one person on it,
without making anything faster. 29 modules now.

- **SeaLegs — new module** (`src/Modules/Crew/SeaLegsModule.cs`, `[SeaLegs]`, side Both).
  *With a crew aboard, your longship points closer into the wind — you can tack where a lone sailor must row.*
  **Cone narrowing only. Nothing gets faster.** Vanilla's `Ship.GetWindAngleFactor()` is
  `num = Vector3.Dot(EnvMan.instance.GetWindDir(), -transform.forward)`;
  `num2 = Mathf.Lerp(0.7f, 1f, 1f - Utils.Abs(num))`;
  `num3 = 1f - Utils.LerpStep(0.75f, 0.8f, num)`; `return num2 * num3`
  (with `Utils.LerpStep(l,h,v) = Clamp01((v - l) / (h - l))`). The postfix **recomputes that expression verbatim and
  replaces only the LerpStep pair** — `num2` (the off-wind floor: 1.0 beam-on, falling to 0.70 dead upwind and dead
  downwind), `m_sailForceFactor` and `Speed.Full` are never touched, so downwind speed is bit-identical to vanilla at
  every crew size. Crew 1 (or 0) returns without writing the result at all.
  The pair comes from a cone in **degrees**: `hi = cos(deg)`, `lo = hi - 0.05` (vanilla's own ramp width). Defaults
  32° / 27° / 23° for 2 / 3 / 4+ aboard against vanilla's 36.87°, i.e. `LerpStep(0.7980, 0.8480)` /
  `(0.8410, 0.8910)` / `(0.8705, 0.9205)`. Degrees are clamped to **[20, 36.87]**: the dead zone can never close
  (straight upwind is still oars) and can never be made wider than vanilla, and the table is forced monotonic so more
  crew can never point worse than fewer.
  **Crew count comes from the ship's ZDO, never from `m_players.Count` on a reader.** `Ship.m_players` is filled by
  the local physics scene (`OnTriggerEnter`/`OnTriggerExit`), so at a zone edge two clients legitimately hold
  different lists and anything derived per client would desync. The ship **owner** — the one machine that also runs
  `Ship.CustomFixedUpdate`, which early-returns on everyone else — writes the count (helmsman included, clamped 0–8)
  into ZDO int `nvlb_crew` from a postfix on `Ship.UpdateOwner`, which vanilla already runs on an
  `InvokeRepeating("UpdateOwner", 2f, 2f)` started in `Ship.Start`, and only when the number actually changed. Everyone
  (owner included) reads it back in the wind-angle postfix, so the owner's sail force and the passenger's sail
  animation agree.
  **Visual: the minimum.** No sounds, no messages, no vignette — vanilla already lerps `Hud.m_shipWindIcon`'s colour by
  `GetWindAngleFactor()`, so the helmsman simply sees the wind arrow brighten when the crew makes the sail catch. The
  one addition is a postfix on `Hud.UpdateShipHud(Player, float)` that gives **passengers** that same read-only gauge —
  `m_shipWindIndicatorRoot` / `m_shipWindIconRoot` / `m_shipWindIcon` driven from the ship they are standing on, with
  the rudder and sail controls hidden (local `PassengerWindGauge=true`). It restores the vanilla HUD unconditionally on
  every frame it is not showing the gauge — including the frame you take the tiller — and only tracks the two objects
  vanilla never re-activates itself.
  `[SeaLegs] SelfTest=true` (local) logs the whole cone table at load — the LerpStep pair and resulting degrees for
  crew 1..4, plus the beam-on and dead-downwind invariants — pure arithmetic, so a headless server can check it.
  Settings: `Crew2Cone=32`, `Crew3Cone=27`, `Crew4Cone=23`, `MaxCrewCounted=4`, local `PassengerWindGauge=true`,
  local `SelfTest=false`.

- **Lookout — new module** (`src/Modules/Crew/LookoutModule.cs`, `[Lookout]`, client-side).
  *Stand off the tiller on a moving ship and you see further — the map reveals wider for the lookout.*
  Extra map-fog radius for the local player and **nothing else**: no broadcast line, no automatic pins, no serpent
  callout, no voyage stat, never `ExploreAll`. Completely client-local — no ZDO, no RPC, no shared state — so two
  clients with different settings (or one with none) can never disagree about anything.
  Hook: prefix + postfix on `Minimap.UpdateExplore(float dt, Player player)`, whose vanilla body reads the radius
  straight off `m_exploreRadius`; the prefix scales that field for the duration of the one call and the postfix puts
  back the exact float it saved. A postfix that called `Explore()` a second time was rejected — `UpdateExplore` runs
  every frame while only the timer decides whether the O(r²) pixel loop actually fires. Three guards against a leaked
  scale: the restore is unconditional (not gated on `Active`), the prefix self-heals a value left behind by a call that
  never reached its postfix, and `m_exploreRadius` is read from exactly one place in the whole game.
  The helmsman (`player.GetControlledShip() != null`) always gets the vanilla radius. No "stand at the bow" rule and no
  role system: anyone aboard who is not steering is the lookout.
  Settings: `RadiusMultiplier=1.75` (clamp 1.0–3.0, 1.0 = off), `RequireMoving=true`, `MinSpeed=1.0`.

- Docs: `docs/MODULES.md` gains the two rows (29 modules + `Tiers` = 30 rows), both READMEs gain an
  **On the water** section.

## 0.4.9 (2026-09-08)
- **RepairAll — new module** (`src/Modules/Repair/RepairAllModule.cs`, `[Repair]`, client-side; 27 modules now).
  Open a workbench, forge, artisan table or any other crafting station and **every item in your inventory that
  this station is allowed to repair is repaired in one go** — no more clicking the little hammer once per
  damaged item.
  Vanilla's rules are not relaxed anywhere. Eligibility is decided by calling vanilla's OWN private
  `InventoryGui.CanRepair(ItemDrop.ItemData)` directly (visible at compile time through the assembly
  publicizer) rather than a re-implementation that could drift from it, and the candidate list is vanilla's
  own `Inventory.GetWornItems()` ("uses durability AND below max"). So repairs stay **free**, a forge still
  cannot repair a workbench item, a station below the recipe's `m_minStationLevel` still refuses, and each
  item is healed exactly the way `InventoryGui.RepairOneItem()` heals it — `RaiseSkill(Crafting, 1 -
  durability/max)` then `m_durability = GetMaxDurability()`. Equipped items are repaired, as in vanilla.
  One **station repair effect** (`CraftingStation.m_repairItemDoneEffects`, the same EffectList vanilla plays
  per item) and one **top-left message** per batch — "Repaired 7 items", via vanilla's own `$msg_repaired`
  localisation — never per-item spam. The repair button is left dead and un-glowing straight after the batch,
  exactly as `OnRepairPressed`'s follow-up `UpdateRepair()` would have left it.
  Hook: a postfix on **`InventoryGui.UpdateRepair()`** — the only vanilla path that has already resolved the
  station (`Player.m_localPlayer.GetCurrentCraftingStation()`; `InventoryGui.Show(Container)` has not), and the
  one `InventoryGui.Update()` calls every frame while the GUI is visible, so a single hook covers both "the
  station GUI just opened" and "the repair panel refreshed while it is open", and samples the hotkey.
  The batch arms once and disarms after firing. It re-arms on `InventoryGui.Hide()` (postfix), on losing the
  current station, and when the player inventory's item **count** changes (you dragged a damaged item out of a
  chest) — deliberately *not* on durability, because an equipped torch drains every frame and a naive
  "repair whatever is repairable" would machine-gun the repair sound at a workbench. A 0.25 s floor between
  batches backs that up, and any throw in this per-frame GUI path logs once and disables the module for the
  session rather than spamming the log.
  NVLB's **ExtraSlots are real cells of the player's own `Inventory`**, not a second container, so gear parked
  in an equipment / utility / generic slot is covered automatically, with no extra code. Building pieces and
  the hammer are deliberately **out of scope** — nothing standing in the world is ever touched.
  New settings: `[Repair] Enabled=true`, `Trigger="OnOpen"` (`OnOpen` | `Hotkey` | `Both`) and
  `ShowMessage=true`, all server-synced; local `Hotkey="R"` (Unity KeyCode name, or `None`) and
  local `SelfTest=false`, which flips the module's side to Both so a dedicated server logs every repairable
  item in `ObjectDB` grouped by the station and station level vanilla would demand — a headless proof of the
  eligibility rules with no client and no game state touched. Turning `[Repair] Enabled` off is live.

## 0.4.8 (2026-09-08)
- **DualPowers / CombatRecharge — the Forsaken-power HUD, upgraded** (`src/Modules/Powers/PowerRing.cs`, new).
  CombatRecharge already turns a fight into cooldown recovery, but vanilla shows that as a square icon and a
  shrinking number, so nobody could see it happening. Every power icon — vanilla's slot 1 *and* DualPowers'
  cloned slot 2/3 — is now drawn as a **circle** with a thin **charge ring** around its edge that fills as the
  cooldown recovers: full ring = ready, and every second a landed or taken hit shaves off makes the ring jump
  forward. One soft pulse at the moment the power comes ready, nothing animating while idle.
  Still **no shipped UI assets**: both sprites (an antialiased disc for the circle mask, an annulus for the ring)
  are generated at runtime into a 256×256 RGBA32 `Texture2D` and cached for the process. The ring is a plain
  Unity `Image` with `type=Filled` / `fillMethod=Radial360` / `fillOrigin=Top`, `fillAmount = 1 - remaining/total`,
  where *total* is the power's own `StatusEffect.m_cooldown` (times `CooldownMultiplier`), never a hardcoded
  20 minutes — the ring self-corrects if the remaining time ever exceeds it.
  The vanilla icon's sprite and colour are never replaced: the icon object is moved inside a generated
  circle `Mask` and put back — parent, sibling index, anchors, pivot, size — the moment the decoration is torn
  down. Because PowerHud maps the cloned widget's leaves by component index, it now un-decorates before cloning
  and the ring re-applies on the same frame, so the clone can never inherit or duplicate the decoration.
  New, all **machine-local** (cosmetic, per player, never server-synced) and all live on a config edit:
  `[Powers] RoundIcons=true`, `CooldownRing=true`, `RingThickness=4` (pixels, 1–24), `RingColor="E6C88AD9"`
  (Valheim's warm parchment gold at 85% alpha) and `RingTrackColor="00000066"` for the un-filled remainder.
  `RoundIcons` and `CooldownRing` are independent — either alone works.
  Robustness, because this runs inside `Hud.UpdateGuardianPower` every frame: one throw anywhere logs once,
  tears the decoration down and permanently disables it (the powers themselves keep working); a widget that has
  not been laid out yet is retried instead of failing; a non-square icon rect uses the smaller side; a Hud
  rebuild, `ShowHud` toggle or player respawn re-decorates lazily. A client logs
  `[Powers] HUD ring: decorated slots=… icon=…px ring=…px …` once when it builds. `[Powers] SelfTest=true` now
  also proves the disc/annulus coverage maths and the sprite generation headlessly on a dedicated server.

## 0.4.7 (2026-09-07)
- Docs only: the mod page now leads with what it is — the ultimate Valheim quality-of-life mod — with the catch-up
  story as the "why" underneath. No code change. Version bumped so Thunderstore takes the new page; the server's
  version check means everyone updates to 0.4.7 together (r2modman → Update, or import the zip).

## 0.4.6 (2026-09-07)
- **FistsAndShields** `[Fists]` (new module): fist weapons can be used with a shield. Vanilla declares every fist weapon
  `ItemType.TwoHandedWeapon`, and `Humanoid.EquipItem` branches on that type, so equipping fists drops your shield and
  equipping a shield drops your fists — even though bare fists (which are not an item at all) have always worked with a
  shield, which is the proof that the unarmed animation set is shield-compatible. The module flips one field per item
  prefab in `ObjectDB` — `m_shared.m_itemType` `TwoHandedWeapon` → `OneHandedWeapon` — for everything whose skill is
  `Unarmed`, so vanilla's Flesh Rippers and every modded fist weapon (Hugo's Armory / Shapekeys_and_More leather, deer,
  bronze, iron, silver and black-metal fists) are covered automatically; `[Fists] ExtraPrefabs` / `ExcludePrefabs` adjust
  the list by prefab name or item name. `SharedData` is per-prefab, so items already in a chest or on a character pick it
  up with no migration.
  What the change does and does not touch, from a grep of the whole decompile (`TwoHandedWeapon` appears in three files,
  `IsTwoHanded` in two): damage, skill, attack, block values and the fist weapon's own animation state are untouched
  fields; with a shield the animation state comes from the shield, exactly as vanilla bare-fists-plus-shield already does;
  `GetCurrentBlocker` still blocks with the fists when no shield is held; `IsTwoHanded()` is used in exactly one place
  (auto-equip on pickup) and only against the *left* hand, which fist weapons never occupy; the in-hand claw visual is
  attached by hand slot and item hash, never by item type. Two visible side effects, both intended: the tooltip now says
  one-handed instead of two-handed, and a torch will now sit in your off-hand alongside fists. Nothing serialises the item
  type — a save/ZDO stores the prefab name and `m_shared` is resolved from the prefab on load — so there is no migration
  and no ServerSync compatibility concern.
- **CraftFromChests**: the stone oven pulls from nearby chests, meat racks still do not. `[Chests]
  PullForCookingStations` gated every prefab carrying a `CookingStation` component together — the meat racks
  (`piece_cookingstation`, `piece_cookingstation_iron`) *and* the oven (`piece_oven`) — so bread dough could not be baked
  through the storage wall without also feeding the group's raw meat to the racks. New `[Chests] PullForOvens=true` +
  `OvenPrefabs="piece_oven"` is a per-prefab exception: a cooking station pulls if `PullForCookingStations` is true **or**
  if `PullForOvens` is on and its prefab is listed. `PullForCookingStations` keeps its old "all of them" meaning. The
  cauldron (`piece_cauldron`) and CookingAdditions' `BCA_CookingPot` are `CraftingStation`s, not `CookingStation`s — they
  never reached this gate and already pull via `PullForCrafting=true`. `[Chests] SelfTest` now prints the gate that
  applies to every cooking-station prefab in the build, and confirms the cauldron/pot are crafting stations.

## 0.4.5 (2026-09-07)
- **FastMining/OreRegrowth**: fractured ore stages (`rock4_copper_frac` etc.) handled — mining is now fast after the first hit
  and regrowth records the fully-mined frac, respawning the original vein; **DualPowers**: Shift+interact sets slot 1, altar
  messages name the slot and key, HUD shows the slot-2 key, `nvlb.power clear/swap`.
- **CorpseRunPlus**: the grave compass is an off-screen waypoint — it sits on the grave while the grave is on screen and slides
  to the screen edge in its direction when it is not (`[CorpseRun] CompassMode=Edge`, `CompassEdgeMargin=60`; `Fixed` restores
  the old static arrow), and holding `[CorpseRun] ClearGraveKey` (default `Delete`) for `ClearGraveHoldSec` dismisses the grave
  marker and Grave Pull without the console.


## 0.4.4 (2026-09-07)
- TestCommands module (server-gated `[Debug] AllowTestCommands`, default off): `nvlb.give`, `nvlb.power`, `nvlb.tier`
  for testing on private servers.

## 0.4.3 (2026-09-07)
- Docs: rewritten Thunderstore page (features by theme, customization guide). No code changes.

## 0.4.2 (2026-09-07)
- **FoodNoDecay**: decay is now removed at the source. 0.4.1 let vanilla decay a food and then raised the value back afterwards,
  which pushed a rising max through `SetMaxHealth`/`SetMaxStamina`/`SetMaxEitr` every second and made the health/stamina bars
  pulse - lowering and refilling - once a second. The decay curve inside `Player.UpdateFood` is replaced instead, so the values
  never move and the bars are static. New `[Food] HidePulse` (default on) also stops the food icons and their countdowns
  flashing in the HUD; `[Food] PulseBelowSeconds` (default 0 = never) can keep the flash as a last-seconds warning.
- **ExtraSlots**: the bottom row is now two plain storage slots instead of the Z/X/C quick slots - `[Slots] GenericSlots = 2`,
  `[Slots] QuickSlots = 0`. Anything fits in them, they have no hotkey and no label. The quick-slot code path is untouched:
  setting `QuickSlots` above 0 brings the hotkey row back. Items saved by 0.4.1 in `quick1`..`quick3` are migrated into the
  matching generic slots on load, with a log line.
- **CorpseRunPlus**: grave features only activate after a death in this world; cleared on loot. The compass, Grave Pull and
  the scaled CorpseRun buff used to key off `PlayerProfile.HaveDeathPoint()`, which vanilla never clears - so an old character
  joining a new world arrived with the compass already pointing at a death spot from somewhere else. The grave is now recorded
  by the mod, stamped with the world name, and deleted when the grave is looted or by the new `nvlb.grave.clear` command.

## 0.4.1 (2026-09-07)
- **ExtraSlots**: proper side panel UI in the vanilla style, adapted from shudnal's ExtraSlots (public domain); main grid back
  to 4 rows. The extra slots now sit in their own wooden panel beside the inventory window — equipment in columns
  (Head/Chest/Legs, Back/Utility/Utility), a food column with fork icons, an ammo column with arrow icons, and the quick slots
  labelled with their own hotkeys underneath. Short horizontal labels instead of the vertical text of 0.4.0.
- New per-player settings `[Slots] PanelOffsetX`, `PanelOffsetY`, `PanelScale`. `[Slots] ShowUI = false` still turns the
  drawing off; the slots then fall back to plain extra rows under the bag and every item stays reachable.

## 0.4.0 (2026-09-07)
- **ExtraSlots** `[Slots]`: equipment slots (helmet/chest/legs/cape), 2 utility slots (Megingjord + Wishbone together), 3 food
  slots (auto-eat), 2 ammo slots, 3 quick slots (Z/X/C). Items are stored in the character's custom data, never in the vanilla
  grid, so a vanilla client cannot delete them; 3 rolling backups + `nvlb.slots.restore`; rescues items left behind by
  shudnal's ExtraSlots. Replaces ExtraSlots + ConditionalConfigSync + YamlDotNet.
- **Loadouts** `[Loadouts]`: Ctrl+V / Ctrl+B save the weapon+shield in hand; V / B equip both with one key.
- **CorpseRunPlus** `[CorpseRun]`: grave compass (HUD arrow + distance), respawn with Bread already eaten and 10 min Rested,
  Grave Pull (stamina regen up / drain down, strongest far from your grave, fading as you approach), and the vanilla Corpse Run
  buff scaled by distance from grave to home. Enemies are unchanged.
- Every number is server-synced and hot-reloads on the server; hotkeys and HUD offsets are per-player.


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
