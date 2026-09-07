# Third-party code and credits

## Vendored source

### ServerSync (`src/Vendor/ServerSync.cs`)

- Author: **blaxxun-boop**
- Source: https://github.com/blaxxun-boop/ServerSync
- Commit vendored: `c57c2aa54e07cdcc7630d6068699ea781622323e` (2025-04-06)
- License: **MIT-0** (MIT No Attribution) — full text in `src/Vendor/ServerSync-LICENSE.txt`
- Not modified. Vendored as source (rather than built as a separate DLL and merged) so it
  compiles cleanly in the same headless build pipeline as the rest of NoVikingLeftBehind. Do not edit
  this file directly — re-copy from upstream if it needs to change, so it stays diffable.

## Adapted code

### AzuCraftyBoxes

- Author: **Azumatt**
- Source: https://github.com/AzumattDev/AzuCraftyBoxes
- License: **MIT-0** (MIT No Attribution)
- NoVikingLeftBehind's `CraftFromChests` module (`src/Modules/Chests/`) adapts AzuCraftyBoxes'
  approach to pulling crafting materials out of nearby containers — container
  discovery/ownership handling and the inventory-requirement patch points in particular. The code
  was re-written against this mod's `FeatureModule` contract rather than copied wholesale, but it
  is close enough in structure that it is credited here as adapted code, not merely as an idea.
  MIT-0 requires no attribution; this entry is voluntary.

### ExtraSlots

- Author: **shudnal**
- Source: https://github.com/shudnal/ExtraSlots (Thunderstore: `shudnal/ExtraSlots`)
- License: **Unlicense** (public domain)
- NoVikingLeftBehind's extra-slot **panel UI** (`src/Modules/Slots/SlotsUi.cs`, plus the panel
  geometry in `SlotLayout.ComputePanel`) is adapted from shudnal's `EquipmentPanel.cs`: the idea of
  re-positioning the vanilla `InventoryGrid` cells for the extra rows into a panel of their own
  instead of building a second grid, the background cloned from the inventory window's `Bkg` and
  `Darken`, the `binding`-label stretch that makes the captions render horizontally, the drag
  "unfit" tint, the quarter-tile slot geometry, and the empty-food-slot fork hint. The storage
  model underneath (`SlotStore` / `SlotBlob` / `SlotsRescue`) is NoVikingLeftBehind's own and
  shares no code with ExtraSlots. The Unlicense requires no attribution; this entry is voluntary.

## Runtime dependency (not bundled)

- **BepInEx** / **BepInExPack_Valheim** (denikson), 5.4.2333 — LGPL-2.1 (BepInEx core). Not
  distributed with this mod; required separately (see the Thunderstore dependency in
  `thunderstore/manifest.json`).
- **Harmony (Lib.Harmony / 0Harmony)** — MIT, distributed as part of BepInEx, referenced but
  not bundled.

## Build-time only (not shipped in the plugin DLL)

- `Microsoft.NETFramework.ReferenceAssemblies` (Microsoft, MIT) — compile-time only.
- `BepInEx.AssemblyPublicizer.MSBuild` (BepInEx project, MIT) — compile-time only, rewrites
  reference assemblies so private members are visible; does not affect the shipped DLL.
