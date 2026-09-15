# Settings menu — Simple / Advanced plan (2026-09-15)

Matt's brief: 5,000+ downloads, and the in-game settings tab (Escape → Settings → NoVikingLeftBehind) is overwhelming. Keep an
Advanced view with everything; add a slimmed-down Simple view with only the settings that matter. Three research passes feed this
plan — `docs/research/settings-menu/INVENTORY.md` (all 294 settings classified), `UI-ARCHITECTURE.md` (code design + mockups),
`UX.md` (personas, copy rules, precedent). Decisions Matt still has to make are at the bottom.

## What the research found

- **The tab is already per-module.** The right pane only ever shows one module (max 35 rows). The overwhelm is the LEFT list:
  42 module rows, every one equally prominent, named after code modules. So Simple is a new left list of ~7 plain-language groups
  and a right pane that ignores modules; Advanced is today's page untouched.
- **294 settings split cleanly:** 33 Essential · 188 Advanced · 32 Diagnostic (22 SelfTest flags, Diagnostics, TierOverride,
  RemoveKeys, Mode/HotReload, one obsolete key) · 41 module on/off toggles (6 of them diagnostic-only modules).
- **Knobs multiply behind the player's back.** Build cost = Discount × Settlement × Builders yard × Builder skill × Rhythm; at
  defaults a behind-tier stone wall in a yard at Builder 50 mid-streak costs ~12% of vanilla. XP = SkillGainRate × Playtime ×
  SkillCatchup × Vanguard. Simple must expose ONE knob per chain (`[Discount] CostMultiplier`, `[ServerKeys] SkillGainRate`).
- **12 labels mislead** (SkillReductionRate reads as an amount; CostMultiplier's direction is inverted; Food.KeepFraction hides
  that 1.0 = "no decay"; "behind the frontier" appears in ~15 hints and is an NVLB-only concept). Rewrites are in INVENTORY §2.
- **Traps to surface:** `[Discount] Enabled` must be on at startup or Settlement + Builders silently do nothing; `QuickSlots`
  without `QuickSlotKeys` and `Loadouts.Slots=2` without a key do nothing; a module switched off needs a restart to come back on
  (the tab greys it with no reason shown).
- **Real users, from the 8 GitHub issues:** portal ore (#7), auto-eat vs food-no-decay (#2), achievements (#4/#8), refunds (#3),
  craft-from-chests (#1). The Simple view is built around those questions, not around modules.
- **Precedent:** vanilla's World Modifiers screen (presets → sliders → "Custom" the moment you hand-edit) is the model players
  already know. BepInEx ConfigurationManager's buried "advanced" toggle is the anti-pattern (nobody finds it).

## Design (see UI-ARCHITECTURE.md for the mockups)

- **Metadata:** `SettingLevel { Essential, Advanced, Diagnostic }` on `Opt` (default Advanced so nothing is ever lost), set at
  the bind site with `.Simple(SimpleGroups.X, order)` or `.Diag()`. Group names live once in `SimpleGroups.cs`. Module on/off rows
  use the existing overridable `EnabledOpt`. Cfg file format untouched; `cfg.py` unaffected; two new machine-local keys
  `[SettingsMenu] View` (Simple|Advanced, default Simple) and `ShowDiagnostics` (default off).
- **Simple view:** left = the groups (jump targets); right = every Essential row under plain-language headings, each with a `▸`
  "show everything in this module" button that flips to Advanced, selects the module and scrolls to the row.
- **Advanced view:** today's page + the Simple/Advanced switch in the header + a "Show diagnostics" checkbox. Diagnostic rows
  and diagnostic-only modules hidden unless ticked. `[Powers] Slot1Modifier` (obsolete, ignored) hidden unconditionally.
- **Search** is level-blind in both views (all 294 + Network rows), with one hint line saying so.
- **SmoothServer:** Network entry unchanged in Advanced; its `Profile` and shared-map rows appear under "Server & network" in Simple.
- **Copy rules (UX.md §3):** toggles verb-first, numbers noun-first with the value inline and what 1×/0/off means; multipliers
  shown as "2.5×", never a raw field name; "Restart to turn on" tag only where true; "Everyone on this server" / "Just you" tag.
- **Presets ("Building generosity": Vanilla / Relaxed / Generous / Custom)** — designed (INVENTORY §4: exact values per preset,
  one batched write through TweakDoor modelled on "Reset module", one chat line, Custom computed not stored) but **recommended for
  the release after**, once Matt has seen the Simple list in game.

## Proposed Simple view — 32 rows, 8 groups (labels are the player-facing text)

**Progression & XP** — How fast you learn skills (SkillGainRate) · Skill lost when you die (1 = normal, 0 = none) · Keep your gear
when you die · *Achievement-safe: yes/no* (read-only status row from 0.10.3's check)

**Inventory & carrying** — Carry weight · Extra from the Megingjord belt · Dedicated gear slots (restart to turn on) · Food slots ·
Ammo slots · Belt/utility slots · Spare storage slots · Quick slots (hotbar row)

**Powers & hotkeys** — Keys for the quick slots · Forsaken powers you can carry at once (1 = vanilla) · Key for your second power ·
Modifier held with the second-power key · Key for your third power · Modifier held with the third-power key

**Crafting from storage** — Craft and build from nearby chests · How far a chest can be

**Building costs** — Old-gear recipes cost this much (1 = full price, 0.5 = half) · Building gets cheaper the more you build ·
Furthest a workbench reaches

**Gathering & the world** — Mining speed on old ore · Bars per ore when smelting old metal · Days before mined ore comes back ·
Carry old ore (copper, tin, iron…) through portals · Fires burn this many times longer · Food keeps its full benefit until it
runs out

**Death & getting back** — Compass to where you died

**Server & safety** (admin rows greyed for non-admins) — Who may change these settings · Everyone must have this mod installed ·
Back up your character file · Network preset (SmoothServer) · Shared map for everyone (SmoothServer)

Trimmed to Advanced: Playtime MaxBonus, Vanguard DamageReduction, Loadouts Slots, Repair Trigger, Mining
DropMultiplier, Tools PerTierBonus, CorpseRun RespawnFood, Access Announce. Budget enforced by a self-test (≤ 45 Essential rows).

## Work breakdown (from UI-ARCHITECTURE §6)

1. Metadata: `SettingLevel`, `.Simple()/.Diag()`, `SimpleGroups`, catalog fields + `EssentialGroupsForUi()` + TSV columns (~85 lines) — lands first.
2. Tag all 294 bind sites per INVENTORY.md — mechanical, split 4 ways by module folder, plus the 12 label rewrites.
3. `[SettingsMenu] View` / `ShowDiagnostics` (~30 lines).
4. `NvlbSettingsTab.cs`: header switch, `RebuildLeft()`, `Wanted()` filter, Simple grouping, `▸` jump (~220 lines) — ONE agent
   only; the file has a history of one-throw-blanks-the-page and a stack overflow. Keep per-row try/catch and `_rebuilding`.
5. `SmoothServerBridge` allowlist fields (~15). 6. Headless self-test + `nvlb.catalog simple` (~80). 7. Docs.
8. (later release) Generosity presets (~120).

Proof: rebuild 0 errors, catalog count stays 294 (+2 local), self-test green on NEWTEST, then Matt's screenshots (cloned vanilla
buttons have drawn blank captions twice before; the `▸` button must not swallow row tooltips/clicks). Gamepad already unsupported
on this tab — unchanged.

## Decisions for Matt

1. **Ship with 0.10.3 or as 0.11.0?** The four bug fixes are staged now; the settings work is ~600 lines and a visible UI change.
   Recommendation: ship the bug fixes as 0.10.3 as soon as the wisp test passes; ship Simple/Advanced as 0.11.0 right behind it.
2. **Default view = Simple for everyone** (5,000 existing users get a new first page; Advanced is one click; remembered per machine). Recommended yes.
3. **Simple row list** above — cut or add? Especially: keep "Back up your character file"; drop the two SmoothServer rows?
4. **Group names** — 7 as listed, or the UX doc's 6 ("Catching up / Food & eating / Death & difficulty / Inventory & hotkeys / Building costs / Who can change these")?
5. **Presets now or next release?** Recommended next release (0.11.1), after seeing the Simple page in game.
6. **Label rewrites** (INVENTORY §2 table, 12 rows) — apply all in the same release? Recommended yes; they change no values.
