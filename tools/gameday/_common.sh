#!/usr/bin/env bash
# Shared helpers for the game-day scripts. SOURCE this, don't run it.
# Lives in each mod repo under tools/gameday/ and on the box at /opt/modlab/tools/gameday/.
# Everything runs inside a throwaway dotnet SDK container; nothing is installed on the host.

LAB="${LAB:-/opt/modlab}"
TOOLS="$LAB/.tools"
GAMEROOT="$LAB/game"
SDK="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}"
ILSPY_VERSION="${ILSPY_VERSION:-8.2.0.7535}"
# BepInEx core is game-version independent unless Unity itself bumps; default to the test server's.
BEPINEX_SRC_DEFAULT="${BEPINEX_SRC_DEFAULT:-/opt/valheim-test/data/bepinex/BepInEx/core}"
# Game assemblies our patches can live in (searched in this order).
GAME_ASMS="${GAME_ASMS:-assembly_valheim assembly_utils assembly_guiutils assembly_steamworks}"
# Static/extension helpers our code calls WITHOUT a typeof() (so type discovery can't see them),
# but whose drift breaks the build instantly. Always diffed.
ALWAYS_TYPES="${ALWAYS_TYPES:-Version StringExtensionMethods ZDOVars ZDO Utils FileHelpers ZLog GameVersion Vector2i Vector2s ZoneSystem}"

say()  { printf '\n\033[1m== %s\033[0m\n' "$*"; }
info() { printf '   %s\n' "$*"; }
die()  { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# Hard guard: these scripts must NEVER write into a real server tree.
guard_write() {
  local p; p="$(readlink -f "$1" 2>/dev/null || echo "$1")"
  case "$p" in
    /opt/valheim|/opt/valheim/*|/opt/valheim2|/opt/valheim2/*|/opt/valheim-test|/opt/valheim-test/*)
      die "refusing to write inside $p — server #1/#2/NEWTEST trees are read-only to game-day scripts" ;;
  esac
}

# sdk_run [-v host:ctr[:ro] ...] -- <cmd...>   ($LAB is always mounted at /work)
sdk_run() {
  local mounts=()
  while [ "${1:-}" != "--" ]; do [ $# -gt 0 ] || die "sdk_run: missing --"; mounts+=("$1"); shift; done
  shift
  docker run --rm \
    -v "$LAB":/work "${mounts[@]}" \
    -w /work -u "$(id -u):$(id -g)" \
    -e HOME=/work/.tools -e DOTNET_CLI_HOME=/work/.tools \
    -e NUGET_PACKAGES=/work/.nuget/packages \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    -e DOTNET_ROLL_FORWARD=LatestMajor \
    "$SDK" "$@"
}

ensure_ilspy() {
  [ -x "$TOOLS/bin/ilspycmd" ] && return 0
  mkdir -p "$TOOLS/bin"
  info "installing ilspycmd $ILSPY_VERSION into $TOOLS/bin (one time)"
  sdk_run -- dotnet tool install --tool-path /work/.tools/bin ilspycmd --version "$ILSPY_VERSION" >/dev/null 2>&1 || true
  [ -x "$TOOLS/bin/ilspycmd" ] || die "ilspycmd install failed (net6 tool; DOTNET_ROLL_FORWARD=LatestMajor is required to run it)"
}

label_dir()   { echo "$GAMEROOT/$1"; }
managed_dir() { echo "$GAMEROOT/$1/valheim_server_Data/Managed"; }

# Decompile the game's Version type out of a staged label and print the interesting constants.
print_game_version() {
  local label="$1" out
  ensure_ilspy
  out=$(sdk_run -- /work/.tools/bin/ilspycmd -t Version \
        "/work/game/$label/valheim_server_Data/Managed/assembly_valheim.dll" 2>/dev/null) || true
  # NB field prefixes drift between builds (m_networkVersion in 0.221.12 -> c_networkVersion in
  # 0.221.13), so match on the suffix, not the prefix.
  local ver net world player
  ver=$(printf '%s' "$out"   | grep -oE 'CurrentVersion \{ get; \} = new GameVersion\([0-9, ]+\)' | grep -oE '[0-9]+, *[0-9]+, *[0-9]+' | tr -d ' ' | tr ',' '.' || true)
  net=$(printf '%s'  "$out"  | grep -oiE '[a-z]_networkVersion = [0-9]+' | grep -oE '[0-9]+' | head -1 || true)
  world=$(printf '%s' "$out" | grep -oiE '[a-z]_worldVersion = [0-9]+'   | grep -oE '[0-9]+' | head -1 || true)
  player=$(printf '%s' "$out"| grep -oiE '[a-z]_playerVersion = [0-9]+'  | grep -oE '[0-9]+' | head -1 || true)
  printf 'label=%s  version=%s  networkVersion=%s  worldVersion=%s  playerVersion=%s\n' \
     "$label" "${ver:-?}" "${net:-?}" "${world:-?}" "${player:-?}"
  printf '%s|%s|%s|%s\n' "${ver:-?}" "${net:-?}" "${world:-?}" "${player:-?}" > "$GAMEROOT/$label/VERSION"
}

timer_start() { TIMER_T0=$(date +%s); }
timer_end()   { printf '\n[timing] %s: %ss\n' "$1" "$(( $(date +%s) - TIMER_T0 ))"; }
