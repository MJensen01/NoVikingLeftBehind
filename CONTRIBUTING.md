# Contributing to NoVikingLeftBehind

Thanks for taking an interest. NoVikingLeftBehind is a one-person project run alongside a live server, so the
notes below are what make a report or a change easy to act on. None of this is meant to be a gate.

## Reporting a bug (the most useful contribution)

Open an issue. A report that lets me reproduce it usually gets fixed in the next release; a
report I cannot reproduce waits for the next person who hits it. Please include:

- **Game version** as the console prints it (`Valheim l-1.0.14 (network version 40)`), and
  whether it is a dedicated server, a hosted server (which host), or a listen server.
- **Mod version** and **BepInEx / BepInExPack** version.
- **The full `BepInEx/LogOutput.log`** from the machine where it went wrong, as an attachment,
  not a screenshot. The first 100 lines list every plugin that loaded, and the
  `module summary:` line says which modules applied. Redact Steam IDs if you like.
- **What you expected and what happened**, and whether it also happens with only this mod
  installed. If another mod is involved, name it and its version; the fix is often in how the
  two interact, and that is a fix I want to make, not a reason to close the issue.

A stack trace pasted into the issue is great, but the log around it is what tells me why.

## Pull requests

Pull requests are welcome, and issues are just as welcome. If you are not sure whether a
change fits, open an issue first and say what you have in mind; it saves you building
something that will not be merged. Small, focused PRs are much easier to review than one that
does several things.

What a PR needs before it can merge:

1. It builds with `dotnet build src -c Release` (see *Building* below) with zero warnings that
   were not there before.
2. It keeps the conventions in *House rules* below.
3. It says how it was tested: on which game version, dedicated server or listen server, and what
   you did in game. "It compiles" is not a test for a Harmony patch.
4. It does not bump version numbers or touch `thunderstore/manifest.json`. Releases are cut
   from `main` by tag and the version lives in three places that must move together; I do that
   as part of the release.
5. It adds a line under the unreleased heading in `thunderstore/CHANGELOG.md`.

I may edit a PR before merging (wording, a self-test, a config description), and I will say so
in the PR. I may also decline one; if I do I will explain why.

## Building

You need the .NET SDK (8.x is fine, the mod targets `net472`) and a Valheim install to
reference. `src/Directory.Build.props` resolves the game and BepInEx assemblies from two
variables; point them at any local Valheim + BepInEx install:

```powershell
$env:VALHEIM_MANAGED = "C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed"
$env:VALHEIM_BEPINEX_CORE = "<your r2modman profile>\BepInEx\core"
dotnet build src -c Release
python scripts\package.py     # assembles the Thunderstore zip under thunderstore/dist
```

The dedicated-server assemblies work too (`valheim_server_Data\Managed`). CI
(`.github/workflows/build.yml`) downloads the current dedicated-server depot and BepInExPack from
scratch on every push, so a PR that builds locally against the current game will build there.
Copy the resulting `NoVikingLeftBehind.dll` into a BepInEx `plugins/NoVikingLeftBehind/` folder to test it.

## House rules

These come from bugs that actually shipped, so they are firm.

- **Every gameplay number is a setting, and it is server-synced.** Bind it in the module's
  `Bind()` with `BindSynced` (or `BindLocal` only for hotkeys, HUD offsets and other per-player
  things), give it a description a player can understand, and make it hot-reloadable. Hard-coded
  values do not merge.
- **One module per feature.** Subclass `FeatureModule` (see the comment at the top of
  `src/FeatureModule.cs`), declare its `Side` honestly (`Client`, `Server`, `Both`), and let the
  base class own the `[Section] Enabled` toggle. A module must be safe to switch off at runtime.
- **Harmony patches fail loudly at patch time, never silently at runtime.** Resolve targets with
  `AccessTools` and throw from `ApplyPatches` if anything is null. A transpiler asserts the exact
  number of matches it expects and throws on any other count. **Never relax an assertion to make
  a build pass**; a transpiler that matches nothing and does not throw ships broken.
- **The settings tab metadata matters.** New settings default to the Advanced view. Only mark one
  `.Simple(...)` if a player who has never read the README would need it.
- **Do not break the version lock.** `MinimumRequiredVersion` equals the plugin version on
  purpose: a server and its clients must run the same NoVikingLeftBehind. Anything that lets a
  mismatched client join is a bug.
- **Config file format is an API.** Section and key names are what people have in their cfg
  files and in their tooling; renaming one needs a migration and a changelog line.

## Layout

- `src/Modules/<Area>/<Name>Module.cs` — one feature each; `src/FeatureModule.cs` is the base.
- `src/Access/` — the in-game settings tab plumbing and the TweakDoor RPC that applies edits.
- `src/Ui/NvlbSettingsTab.cs` — the settings tab itself. Tread carefully; one unhandled throw
  blanks the whole page for the player.
- `src/Vendor/ServerSync.cs` — vendored ServerSync (config sync + version lock). Do not edit.
- `docs/MODULES.md` — the per-module table (side, section, settings, defaults). Update it when
  you add or change a setting.
- `thunderstore/` — manifest, changelog, icon; `scripts/package.py` builds the zip.

## Licence

By contributing you agree your contribution is licensed under the repository's LICENSE. Third-party
code and its attribution live in `THIRD_PARTY.md`; if you port something in, add it there.
