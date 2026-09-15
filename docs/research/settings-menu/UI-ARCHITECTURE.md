# NVLB settings tab — Simple / Advanced: UI + metadata design

Branch `release/0.10.3`. Files: `src/Config/Opt.cs`, `src/Config/ConfigCatalog.cs`,
`src/Ui/NvlbSettingsTab.cs` (2 492 lines), `src/Ui/SettingsMenuModule.cs`,
`src/Access/SmoothServerBridge.cs`, `src/Access/TweakDoor.cs`.

## 1. Current structure (one page)

**Rows are generated per selected module, not per setting-universe.** The page is two panes
(`Build()`, tab lines 741–902): a left scroll of module rows grouped by `FeatureModule.Theme`
(9 themes, ordered by `ConfigCatalog.ThemeOrder`), then the Network (SmoothServer) row, then
"THE WHOLE MOD" orphan sections (`[General]`, `[Frontier]`, `[Tiers]`). Clicking one sets
`_selected` / `_selectedSection` / `_network` and calls `RebuildRight()`. The right pane shows
only that module's settings (`Wanted()`, line 1530), in catalog/bind order, with a `[Section]`
heading. **~300 rows are never on screen at once** — the worst module is CorpseRunPlus at 35,
and search is capped at 80 hits. So scroll performance is a non-issue; the problem is purely
that the left list is 42 entries long and every row is equally prominent.

**Metadata per setting** (`Opt` → `SettingInfo`): Hint (≤12 words), Description (the BepInEx
one, used as the hover tooltip), Label (or CamelCase-humanised key), Live (false ⇒ greyed
"Needs a server restart"), HasRange/Min/Max/Step, Choices, `SettingTier` **Everyone|Admin**
(permission, *not* prominence), FreeText, `PickerSpec`, plus derived Owner / ModuleName /
Theme / IsLocal / IsEnabledToggle / ForeignTag.

**Row control kinds** (`BuildRow`, 1588): bool → toggle; enum or string-with-choices → `< value >`
cycler; number → text box (+ slider when `HasRange`); string → text field, or a `KeyRecorder`
for key settings, or a `ListPicker` button with a "Raw" escape hatch. Every row also has a ↺
reset button and a hidden "why not" note that replaces the control when the row is read-only.

**Search** is a header input; each keystroke re-runs `RebuildRight()` over the whole catalog via
`ConfigCatalog.Matches` (key/section/label/hint/description/module), capped at 80, sorted by
section+key, with the Network rows appended.

**Synced vs local vs admin**: never hidden, always shown. `[SettingsMenu] ShowUnavailable`
(default on) keeps unchangeable rows visible and greyed with the reason (`WhyNot`, 2191). The
tooltip says "Shared with everyone on the server" / "Saved on your own machine" / "Admins only".

**Change path** (do not disturb): a control only **queues** into `_pending`/`_pendingOrder`
(tinted row + "• " bullet); Save/OK sends each through `TweakDoor.Request` → routed RPC → server
permission + validate + rate-limit → `CfgFile.SetValue` (timestamped backup) → entry set in
memory → ServerSync push → chat announce + audit line; `_inflight` holds the sent value on screen
for 5 s. Undo is a server-side 20-deep-per-key stack; "Reset module to defaults" is one batched
`CfgFile.SetValues` with one announce line — **the precedent for presets**.

**Known limits**: no `ScrollRectEnsureVisible`, no `Navigation` on any control ⇒ **gamepad
cannot drive this tab at all** (true today, unchanged by this work). Labels span 12px → 344px
from the right (`RightInset`), reflowed on width change with a 120px floor. Cloned vanilla
buttons lose their caption if built without an explicit size (0.7.4 bug) — any new button must
pass a font size. Adding a raycast target on top of a control kills its clicks (0.8.3).

## 2. Tier metadata — the smallest change

`SettingTier` already means *permission* and `src/Tiers.cs` means *world tier*, so a third
"Tier" would be actively confusing. Use **`SettingLevel`**, added to `Opt.cs` (~25 lines):

```csharp
internal enum SettingLevel { Essential, Advanced, Diagnostic }

// on Opt:
public SettingLevel Level = SettingLevel.Advanced;   // default: nothing is ever lost
public string SimpleGroup;                            // plain-language heading (Essential only)
public int SimpleOrder = 50;                          // within the group; ties keep bind order

/// <summary>Show this on the Simple view, under a plain-language heading.</summary>
public Opt Simple(string group, int order = 50)
{ Level = SettingLevel.Essential; SimpleGroup = group; SimpleOrder = order; return this; }

/// <summary>A knob only someone debugging the mod wants. Hidden unless "Show diagnostics".</summary>
public Opt Diag() { Level = SettingLevel.Diagnostic; return this; }
```

Group names live once, with their order, in a new `src/Config/SimpleGroups.cs` (~25 lines) so
60 bind sites can't misspell one and reordering is a one-line edit:

```csharp
internal static class SimpleGroups
{
    public const string CatchUp="Catching up with friends", Inventory="Inventory & carrying",
        Gathering="Gathering & building", Death="When you die", Sailing="Sailing",
        Combat="Combat & powers", Server="Server & network";
    public static readonly string[] Order = { CatchUp, Inventory, Gathering, Death, Sailing, Combat, Server };
}
```

A module's Bind call changes by one chained call — nothing else:

```csharp
// before
BindSynced("RegrowDays", 7f, "…long description…",
           Opt.N("Days before a mined ore node comes back", 0, 60, 0.5f));
// after
BindSynced("RegrowDays", 7f, "…long description…",
           Opt.N("Days before a mined ore node comes back", 0, 60, 0.5f)
              .Simple(SimpleGroups.Gathering, 20));
```

A module whose whole point is its on/off switch needs **no new API** — `FeatureModule.EnabledOpt`
is already overridable: `protected override Opt EnabledOpt => base.EnabledOpt.Simple(SimpleGroups.Gathering, 10);`

`ConfigCatalog.Build()` copies the three fields onto `SettingInfo`; add
`EssentialGroupsForUi()` (groups in `SimpleGroups.Order`, rows by SimpleOrder then bind order),
two counts in `SummaryLine()`, and two **appended** TSV columns (`level`, `simplegroup`).
`SmoothServerBridge.Allowed` gets the same two fields, set in `Build()` — foreign rows aren't in
the catalog. Total ≈ 60 lines outside the UI.

## 3. View design

Two new machine-local settings in `[SettingsMenu]` (`SettingsMenuModule.Bind`, ~20 lines):
`View` (enum Simple|Advanced, **default Simple**) and `ShowDiagnostics` (bool, default false).
Machine-local ⇒ no server round trip, no announce, no admin check; they persist in the player's
own cfg exactly like `ShowUnavailable` does today.

**Header** gains a two-button segmented control (`Simple` / `Advanced`, the lit one drawn with
`SelectedBarColor()`), and — only in Advanced — a "Show diagnostics" checkbox. Switching view
writes the local entry and calls `RebuildLeft()` + `RebuildRight()`.

**Simple view.** Left pane = the ≤7 Simple groups as jump targets (reusing the existing clickable
theme-heading behaviour, `MakeHeaderClickable`/`ScrollLeftTo`). Right pane = **all** Essential
rows, grouped under plain-language headings, ignoring module selection. Each row carries a
`▸` button (34px, left of ↺; `RightInset` 344 → 384 in this view) whose tooltip is "Show
everything in <module>" and which calls `JumpToAdvanced(info)`: set view = Advanced, select the
owning module (or section / the Network panel), rebuild, scroll the right pane to that row.
Target size ≈ 35–45 Essential rows.

**Advanced view.** Exactly today's page — same left module list, same per-module right pane,
same Save/Discard/Undo/Reset footer — with one filter added to `Wanted()`:
`if (s.Level == Diagnostic && !ShowDiagnostics) continue;`. A module whose visible row count
would be zero is dropped from the left list (its Enabled toggle is Essential anyway, so it is
still reachable from Simple).

**Search** is deliberately level-blind in **both** views: it searches all 294 settings including
Diagnostic ones, renders results in the right pane as today (cap 80, Network rows appended), and
prints one hint line above them — "Search looks at every setting, including advanced ones." A
search that can't find a thing the player knows exists is worse than a long list.

**SmoothServer.** The Network entry stays exactly where it is at the bottom of the left list in
Advanced. In Simple, two of its five rows (`Network preset`, `Shared map`) are tagged
`.Simple(SimpleGroups.Server)` in the bridge's allowlist and appear under "Server & network";
the other three (compression, smooth motion, require-the-client-mod) stay Advanced-only. The
"Reset network to Default" button remains on the Network panel only.

### Mockup — Simple (Valheim settings panel, ≈1140×700 units)

```
┌ Settings ────────────────────────────────────────────────────────────────────────────────────┐
│ Gameplay │ Controls │ Graphics │ Audio │ Accessibility │ [ NoVikingLeftBehind ]               │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search [                         ]   ▐ Simple ▌  Advanced            Recent changes          │
│ Anyone on this server may change settings.  Changes apply live to everyone.  12:04 Erik set… │
├────────────────────────┬─────────────────────────────────────────────────────────────────────┤
│ CATCHING UP WITH FRIE… │ CATCHING UP WITH FRIENDS                                            │
│ Inventory & carrying   │  Catch-up XP for players behind                                      │
│ Gathering & building   │  Extra skill gain while below the group   [═════○════]  1.5   ▸  ↺  │
│ When you die           │  Rubber-band slower players                                          │
│ Sailing                │  Move faster when far from the group      [ ✓ ]                ▸  ↺ │
│ Combat & powers        │ INVENTORY & CARRYING                                                 │
│ Server & network       │  Extra inventory rows                                                │
│                        │  Rows added below the vanilla four        [═○════════]  2     ▸  ↺  │
│                        │  Carry weight                                                        │
│                        │  How much you can carry before you slow   [════○═════]  400   ▸  ↺  │
│                        │  Craft from nearby chests                                            │
│                        │  Pull materials out of chests within range[ ✓ ]                ▸  ↺ │
│                        │ GATHERING & BUILDING                                                 │
│                        │  Cheaper building                                                    │
│                        │  What a piece costs, as a fraction        [══════○═══]  0.75  ▸  ↺  │
│                        │  Ore nodes grow back                      [ ✓ ]                ▸  ↺ │
│                        │  Days before a mined node comes back      [═══○══════]  7     ▸  ↺  │
│                        │                                                          ▾ 14 more  │
├────────────────────────┴─────────────────────────────────────────────────────────────────────┤
│ [ Save changes ] [ Discard ] [ Undo last change ] [ Reset module to defaults ]  Nothing wait… │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Mockup — Advanced (today's page + two header controls)

```
┌ Settings ────────────────────────────────────────────────────────────────────────────────────┐
│ Gameplay │ Controls │ Graphics │ Audio │ Accessibility │ [ NoVikingLeftBehind ]               │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search [                         ]     Simple  ▐ Advanced ▌   [ ] Show diagnostics            │
│ Anyone on this server may change settings.  Changes apply live to everyone.  12:04 Erik set… │
├────────────────────────┬─────────────────────────────────────────────────────────────────────┤
│ CATCHING UP            │ [OreRegrowth]                                                        │
│  GroupSkillCatchup [✓] │  Enabled                                                             │
│  PlaytimeRubberBand [✓]│  Turn ore regrowth on or off             [ ✓ ]                    ↺ │
│ INVENTORY              │  Regrow days                                                         │
│  ExtraSlots        [✓] │  Days before a mined ore node comes back [═══○══════]  7          ↺ │
│  SafeSlots         [✓] │  Ore nodes                                                           │
│  AmmoHud           [✓] │  Prefab names that regrow                [ 12 picked ▾ ] [Raw]    ↺ │
│  Loadouts          [✓] │  Max per zone                                                        │
│ BUILDING & GATHERING   │  Cap on nodes regrown in one zone        [══○═══════]  25         ↺ │
│ ▐BuildersGuild     [✓]▌│  Min distance from a player                                          │
│  OreRegrowth       [✓] │  Never regrow this close to someone      [════○═════]  32         ↺ │
│  StackInsert       [✓] │  Announce regrowth in chat               [   ]  Only a server admin  │
│  WorkbenchReach    [✓] │                                                                      │
│  …32 more modules…     │                                                                      │
│ NETWORK                │                                                                      │
│  Network (SmoothServ…) │                                                                      │
├────────────────────────┴─────────────────────────────────────────────────────────────────────┤
│ [ Save changes (2) ] [ Discard ] [ Undo last change ] [ Reset module to defaults ] 2 waiting │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 4. Presets — "Generosity" (optional, phase 2)

A cycler at the top of Simple: `< Vanilla | Relaxed | Generous | Custom >`. A static table
(`src/Config/Generosity.cs`) maps each name to ~8 `(settingId, value)` pairs. **"Custom" is
computed, not stored**: the row shows the first preset whose every member equals its current
value, else "Custom" — no new config entry, nothing to keep in sync.

Applying must **not** queue 8 separate tweaks (8 chat lines, 8 rate-limit tokens, an Undo that
only rewinds one). Instead add `NVLB_Preset(name)` to `TweakDoor`, modelled line-for-line on the
existing `ResetModule` (which already does exactly this): one permission check, one rate token,
**one** `CfgFile.SetValues` write with one backup, per-key `SetInMemory` + `RecordUndo`, and
**one** announce — `[NVLB] Erik set Generosity to Generous (7 settings)`. ≈70 lines.

Risks: (a) it silently overwrites hand tuning — mitigate with the Network panel's two-click
confirm pattern, second caption naming the count ("Click again: changes 7 settings"); (b) Undo
rewinds one member at a time — say so in the tooltip, and offer "set it back to Custom's old
preset" by simply picking the previous preset; (c) a member setting later renamed silently drops
from the table — the self-test in §6 fails the boot log if any preset id is unknown.

## 5. Hot paths that do not move

Nothing in this design touches ServerSync, `TweakDoor`'s permission/validation/rate-limit/
backup/write order, undo, the audit, chat announce, the `ConfigWatcher` hot-reload, admin gating
or `[Access]`. **The cfg file format is untouched**: `SettingLevel`/`SimpleGroup` are compile-time
metadata on `Opt`, never serialised. The only cfg change is three additive machine-local keys in
`[SettingsMenu]` (`View`, `ShowDiagnostics`, plus existing `ShowUnavailable`), which BepInEx
writes on first boot and `cfg.py list/get/set` handles like any other key. `nvlb-catalog.tsv`
gains two **appended** columns (no current consumer parses it — `cfg.py` reads BepInEx's own
type comments, not the TSV). `cfg.py` and the settings tab still write the same file and still
agree.

## 6. Work breakdown

| # | Step | Files | Lines | Parallel? |
|---|------|-------|-------|-----------|
| 1 | `SettingLevel` + `.Simple()/.Diag()`; `SimpleGroups`; catalog fields, `EssentialGroupsForUi()`, summary + TSV columns | `Opt.cs`, `SimpleGroups.cs` (new), `ConfigCatalog.cs` | ~85 | **must land first** |
| 2 | Tag the 294 settings | 41 module files | ~1 line each | **split 4 ways by folder**: Building+Chests+Mining / Slots+Loadouts+Food / CorpseRun+Powers+Vanguard+Fists+Crew / Catchup+Economy+Regrowth+World+Repair+plugin-level. Needs the sibling agent's classification as input |
| 3 | View settings + accessors | `SettingsMenuModule.cs` | ~30 | after 1 |
| 4 | Tab: header segmented control + diagnostics box, `RebuildLeft()` extraction, `Wanted()` branch, Simple grouping headings, `▸` button + `JumpToAdvanced` | `NvlbSettingsTab.cs` | ~220 | **one agent only** — this file has a history of one-throw-blanks-the-page bugs; keep the per-row try/catch and the `_rebuilding` guard |
| 5 | Two allowlist fields | `SmoothServerBridge.cs` | ~15 | with 4 |
| 6 | Headless self-test + `nvlb.catalog simple` | `ConfigCatalog.cs`, `StatusModule.cs` | ~80 | after 2 |
| 7 | Docs | `docs/MODULES.md` §Setting metadata | ~25 | last |
| 8 | (optional) Generosity presets | `Generosity.cs`, `TweakDoor.cs`, tab | ~120 | after 4 |

**Headless test** (step 6, runs on a dedicated-server boot, one log block, no game needed):
every setting has a level; every Essential has a `SimpleGroup` that exists in `SimpleGroups.Order`;
every group has ≥1 Essential; Essential count ≤ 45 (a budget that fails loudly when someone
promotes their favourite knob); no Essential is `Restart()`-only; every Generosity member id
resolves. `nvlb.catalog simple` prints the whole Simple view as text so the classification can
be reviewed from a log. Existing gates still apply: `rebuild.sh` 0 errors, `smoke.sh` on the
throwaway server, `nvlb.catalog` counts before/after must match at 294.

**Only an in-game screenshot can prove**: the segmented buttons render their captions (cloned
vanilla buttons have silently drawn blank twice); the `▸` button's hit rect doesn't swallow the
row tooltip or the control's clicks (the 0.7.6 / 0.8.3 class of bug); labels don't truncate at
`RightInset` 384 at 1080p and ultrawide; group headings read as groups; the scroll knob sizes
correctly on the shorter Simple content. Use `nvlb.uidump` alongside the screenshot.

## 7. Open questions for Matt (recommendations included)

1. **Name.** `Tier` collides twice (`SettingTier` = permission, `Tiers.cs` = world tier). *Recommend
   `SettingLevel` + `.Simple(group)` / `.Diag()` — reads better at the bind site anyway.*
2. **Default view for existing players.** Simple by default flips the page under 5 000 people who
   know where things are. *Recommend default Simple for everyone (it's the point), with the
   Advanced button one click away and the view remembered per machine after the first switch.*
3. **Does Simple show admin-only / restart-only rows?** *Recommend yes, greyed, as today —
   `ShowUnavailable` already governs it, and hiding them makes the mod look like it can't do things.*
4. **Essential budget.** 35–45 rows across 7 groups, ~15 % of the catalog. *Recommend fixing the
   number now and enforcing it in the self-test; the sibling agent's list should be trimmed to fit
   rather than the budget raised.*
5. **Generosity presets now or later?** Cheap (~120 lines, reusing `ResetModule`) but it is the one
   piece that writes several server settings at once. *Recommend shipping the Simple/Advanced split
   first (0.11.0) and presets in 0.11.1 once Matt has seen what Essential actually contains.*
