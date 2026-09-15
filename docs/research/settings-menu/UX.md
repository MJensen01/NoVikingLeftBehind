# NVLB settings tab — UX plan (Simple vs Advanced)

Scope: user-experience only (personas, information architecture, copy, first-run, pitfalls).
Setting inventory and UI code design are covered by sibling agents.

## 1. Personas

**A — The server admin, setting up for the group (first boot, alone, before friends join).**
Wants the mod to feel safe to turn on and not need a second pass later. Touches:
- `EnforceClientMod` — require everyone to run the mod to join
- `[Access] TweakAccess` — let everyone tweak live, or keep it admin-only
- `[ServerKeys] SkillGainRate` / `SkillReductionRate` — how fast people level, how much dying costs
- `[ServerKeys] DeathKeepEquip` — keep gear on death
- `[ServerKeys] AllRecipesUnlocked` / `NoBuildCost` / `NoCraftCost` — the "am I making this too easy" dials
- Achievement-safe status (new in 0.10.3) — "will my world still get achievements"
- `[Regrowth] Enabled` — does ore come back at all

**B — The regular player, opening the tab mid-game to fix one specific annoyance.**
Doesn't know or care about module names; arrives from a real friction moment. Touches:
- "Why can't I carry this ore through the portal" → `[Portals] AllowBehindFrontier` / `AlwaysAllow`
- "Turn off auto-eat" / "auto-eat is wasting my good food" → `[Slots] AutoEatFromFoodSlots`, `AutoEatWhenSecondsLeft`
- Hotkeys: loadout key, second-power key, craft-from-chests toggle key
- "Why did I only get half my materials back" → understanding, not a setting (see Pitfalls)
- Quick-slot row on/off, extra slot counts

**C — The power user, tuning building costs for the group's home base.**
Reads patch notes, wants dials not defaults. Touches:
- `[Builders] YardRadius`, `Materials=` per station, `StructuralFirstCost` / `StructuralAttachedCost`
- `[Builders] SkillMaxDiscount`, `RhythmPerRepeat` / `RhythmMax`
- `[Settlement] PerTierDiscount` / `MaxDiscount`
- `[Discount] CostMultiplier` (recipes behind the frontier)
- `[Carry] BaseCarryWeight` / `BeltBonus`
- `[Workbench] PerTierMetres` / `MaxRangeMetres`

## 2. Simple view — information architecture

~24 rows across 6 headings, ordered by how often a group actually opens the tab for that reason
(catching up and eating first — they generate the most support asks; building and combat last —
they're the power-user's territory). **Admin-only** rows are marked; everything else is open to
any player by default (server can still lock the whole tab to admins with one switch).

**1. Catching up** (admin-leaning, but explains itself to anyone)
- *Let ore and bars ride the portal* — "Carry mined metal through portals once your group has moved past it." (AlwaysAllow items still need Advanced.)
- *Discount on old gear* — "Recipes and pieces behind your group's progress cost less to make."
- *Ore grows back* — "Mined-out veins respawn over time once nobody's nearby." — admin
- *Catch-up bonus for less-active players* — "Whoever's behind the group's average playtime gathers and levels faster."
- *Skill catch-up* — "Skills below the group's best level climb faster, until they catch up."

**2. Food & eating**
- *Auto-eat from your food slots* — "Refills your food automatically when a slot opens up."
- *Food doesn't fade early* — "Meals keep full strength until they actually run out, instead of tapering off."
- *Wait before auto-eating again* — "With food-doesn't-fade on, don't eat again until N seconds are left." (pairs with the row above; show together)

**3. Death & difficulty** — admin-only
- *Skill loss on death* — "How much skill you lose when you die (0 = none)."
- *Keep your gear on death* — "Equipped items stay on your body instead of dropping."
- *XP gain* — "How fast everyone levels, as a multiplier of normal (1× = vanilla)."
- *Achievement-safe* — read-only status: "Yes — these settings won't disable achievements" / "No — here's why" (surfaces the 0.10.3 `nvlb.status` check in the UI itself).

**4. Inventory & hotkeys**
- *Extra equipment slots* — "Helmet, chest, legs and cape slots outside your main bag." — needs restart to turn on
- *Loadout key* — "Press to snap your saved weapon + shield into your hands."
- *Second power slot key* — "Carry a second Forsaken power and fire it with this key."
- *Craft from nearby chests* — "Use materials in storage next to you, not just your bag."

**5. Building costs**
- *Cheaper building near your base* — "Materials cost less to build within reach of your workbench/forge." — the BuildersGuild yard
- *Builder discount grows with practice* — "The more you build, the cheaper it gets, up to a cap."
- *Workbench reach grows with progress* — "Your build radius grows as your group kills bosses."

**6. Who can change these** — admin-only
- *Anyone can tweak live* / *Admins only* — "Let any player adjust settings while playing, announced in chat" vs "Only server admins can change settings."
- *Require the mod to join* — "Reject players who don't have NoVikingLeftBehind installed."

That's 21 rows; leaves headroom to 30 without crowding — a good stopping point rather than padding
to a round number.

## 3. Copy rules

- **Labels:** 2–5 words, plain nouns a non-modder would use ("Extra equipment slots", not
  "ExtraSlots.EquipmentSlots"). Never show a section/key name in Simple; Advanced can show both
  (plain label on top, `[Section] Key` in small type underneath — the search box already indexes
  key/hint/description, so hiding the key from Simple loses nothing).
- **Toggles are verb-first** ("Let ore ride the portal", "Keep your gear on death"). **Sliders/
  numbers are noun-first with the value inline** ("XP gain: 2.5×", "Ore respawn: 14 days"),
  because a slider needs its current state visible without hovering.
- **Multipliers read as a multiplier, not a raw field name:** "2.5×" or "2.5× normal", never
  "SkillGainRate 2.5". Percent discounts read as "50% off", not "CostMultiplier=0.5". Always show
  what 1× / 0% / off means in the helper line so a number alone never has to be decoded.
- **"Needs restart":** a small tag, not a paragraph — `Restart to turn on` next to the toggle,
  shown only for the on-cases that actually need it (per `docs/MODULES.md`, turning a module off
  is always instant; only off→on needs a restart). Never say "needs restart" on a row where it
  doesn't apply — that's the fastest way to make admins distrust the tag everywhere else.
- **Server-wide vs. just-you:** a small right-aligned tag — `Everyone on this server` (default,
  most rows) vs. `Just you` (hotkeys, HUD offsets). Don't repeat it in the helper text; the tag
  carries it.
- **Simple/Advanced switch:** a two-state control at the top of the tab, not a checkbox buried in
  a menu — label it `Simple` / `Advanced` (not "Show advanced settings", which reads as a single
  extra option rather than a whole second view). State persists per player.

## 4. First run

First time *any* admin opens the tab on a fresh install (no cfg customization yet — detect by
"every value still equals its shipped default"), show a one-time strip, not a modal:
*"New here? Simple view covers what most groups touch. Switch to Advanced any time — search
finds settings in both."* Dismiss and it's gone for good, same as the rest of Valheim's onboarding
hints.

**Recommend a preset picker: yes**, modeled directly on vanilla's World Modifiers screen — a
dropdown (Vanilla-safe / Balanced [default] / Generous) that pre-fills the *Catching up* and a
couple of *Death & difficulty* rows, flips to a `Custom` label the moment any bundled row is
hand-edited (exactly how World Modifiers' preset selector behaves), and never auto-reapplies once
custom. Reasons this fits NVLB specifically, not just precedent-for-its-own-sake:
- The mod's whole pitch is "catching up," which is naturally a few correlated dials (discount,
  regrowth, playtime bonus, skill catch-up) — the same shape as vanilla's death-penalty/raid/
  resource cluster, which is exactly what presets are for.
- New admins in the issues (e.g. #8) demonstrably don't know which settings interact with vanilla
  world modifiers; a preset that's honest about what it touches teaches that boundary instead of
  leaving them to find it via a GitHub issue.
- Keep it narrow: the preset must **only** ever touch the rows it visibly lists — never silently
  reach into Advanced or `[ServerKeys]` rows the admin didn't see change.

## 5. Pitfalls (from the issue history)

- **Frontier logic isn't self-explanatory:** issue #7's user assumed "behind the frontier" already
  covered raw ore, but had to be told in a comment to add it to `AlwaysAllow`. Simple's portal row
  must define "behind the frontier" in the helper line, not just name the toggle.
- **Silent overrides of vanilla modifiers** (issue #8): `[ServerKeys]` used to clobber a launch-flag
  world modifier with no visible warning. 0.10.3 fixed the mechanism; the UI should still surface
  it — show a warning row when a `[ServerKeys]` value differs from what the world file already has.
- **Achievement flag confusion** (issue #4): server-only behavior that never showed in single-player
  testing. The new `achievement-safe` status belongs in Simple, not just a console command.
- **Hidden settings people can't find again:** mitigated already — search covers Advanced too
  (`docs/MODULES.md`), so nothing in Simple needs to duplicate an Advanced row "just in case."
- **Presets silently overwriting hand tuning:** never apply a preset without a confirm that names
  exactly which rows will change and what they change from — same reasoning vanilla uses by asking
  before a preset switch discards custom sliders.
- **Advanced going undiscovered:** keep it a always-visible top-level toggle next to Simple, not a
  settings-within-settings submenu — BepInEx's Configuration Manager's own "advanced" toggle is
  often missed precisely because it's a small link at the bottom of a long list.

## 6. Top 5 recommendations

1. Ship the 6-heading, ~21-row Simple view above; everything else moves to Advanced behind search.
2. Put a preset picker (Vanilla-safe / Balanced / Generous) on the *Catching up* + *Death &
   difficulty* rows only, mirroring vanilla's World Modifiers preset → Custom behavior.
3. Surface `achievement-safe: yes/no` as a status row in Simple, not just `nvlb.status` in console.
4. Adopt the copy rules verbatim (verb-first toggles, noun-first sliders with inline values,
   multipliers always spelled out, restart tag only where true) before writing any row text.
5. Pair FoodNoDecay and AutoEatWhenSecondsLeft in the UI, not just in config — issue #2 shows
   players hit this as one problem ("auto-eat wastes my food"), not two settings.

## Sources

- https://haptic.gg/support/games/valheim/world-modifiers-and-presets — preset dropdown +
  post-preset individual adjustment behavior on Valheim's own World Modifiers screen.
- https://gamers.wiki/en/games/valheim/guides/valheim-world-modifiers-explained-presets-global-keys — full preset list (Casual/Easy/Normal/Hard/Hardcore/Immersive/Hammer) and how presets bundle multiple modifiers.
- https://valheim.fandom.com/wiki/World_Modifiers — modifier-by-modifier breakdown used as the vanilla precedent for slider + one-line description pairing.
- https://github.com/shudnal/ConfigurationManager (and https://github.com/sinai-dev/BepInExConfigManager) — the BepInEx Configuration Manager "Advanced" tag/toggle pattern that hides low-traffic settings behind a visible switch.
- https://github.com/valheimPlus/ValheimPlus/blob/development/valheim_plus.cfg and https://www.valheimians.com/article/full-guide-to-valheim-plus-config-settings/ — Valheim Plus's 60+ section, ~368-setting flat structure, used as the "what NOT to do for Simple" counter-example (no simple/advanced split at all, so third-party config-manager tools had to invent one).
- GitHub issues on MJensen01/NoVikingLeftBehind (#1–#8, `gh issue view`) — real user confusion points: portal allow-listing (#7), achievement flag (#4, #8), auto-eat vs. food-no-decay timing (#2), deconstruct refunds (#3), craft-from-chests visibility (#1, #5), ore regrowth silent failure (#6).
