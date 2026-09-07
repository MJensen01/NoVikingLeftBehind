#!/usr/bin/env bash
# gameday step 1 — stage a Valheim server build for the lab to compile/diff against.
#
#   fetch-build.sh <label> --from-managed <dir>            # copy an existing install's Managed dir
#   fetch-build.sh <label> --branch <name> [--betapassword <pw>]
#                                                          # pull from Steam via DepotDownloader
#
# Result: /opt/modlab/game/<label>/valheim_server_Data/Managed  (+ a "Managed" symlink at the top)
#         /opt/modlab/game/<label>/BepInEx/core                 (so the tree is a drop-in /game mount)
#         /opt/modlab/game/<label>/VERSION                      (ver|netver|worldver|playerver)
#
# Examples:
#   ./fetch-build.sh v0_221_12 --from-managed /opt/valheim-test/data/bepinex/valheim_server_Data/Managed
#   ./fetch-build.sh v0_221_13 --from-managed /opt/valheim/data/server/valheim_server_Data/Managed
#   ./fetch-build.sh v1_0      --branch public --  (default branch: omit --branch entirely)
#   ./fetch-build.sh pt        --branch public-test --betapassword yesimadebackups
set -euo pipefail
cd "$(dirname "$0")"; . ./_common.sh

LABEL="${1:-}"; shift || true
[ -n "$LABEL" ] || die "usage: fetch-build.sh <label> (--from-managed <dir> | --branch <name> [--betapassword <pw>])"

MODE=""; SRC=""; BRANCH=""; BETAPW=""; APP=896660; DEPOT=896661
BEPINEX_SRC="$BEPINEX_SRC_DEFAULT"
while [ $# -gt 0 ]; do
  case "$1" in
    --from-managed) MODE=managed; SRC="$2"; shift 2;;
    --branch)       MODE=steam;   BRANCH="$2"; shift 2;;
    --betapassword) BETAPW="$2"; shift 2;;
    --app)          APP="$2"; shift 2;;
    --depot)        DEPOT="$2"; shift 2;;
    --bepinex)      BEPINEX_SRC="$2"; shift 2;;
    --) shift;;
    *) die "unknown arg: $1";;
  esac
done
[ -n "$MODE" ] || { MODE=steam; }   # no --from-managed => Steam, default branch

DEST="$(label_dir "$LABEL")"; guard_write "$DEST"
MANAGED="$(managed_dir "$LABEL")"
timer_start
say "fetch-build: staging '$LABEL' -> $DEST"
mkdir -p "$MANAGED" "$DEST/BepInEx"

if [ "$MODE" = managed ]; then
  [ -d "$SRC" ] || die "no such Managed dir: $SRC"
  info "copying $SRC"
  rsync -a --delete "$SRC/" "$MANAGED/"
else
  DD="$TOOLS/dd/DepotDownloader"
  if [ ! -x "$DD" ]; then
    info "downloading DepotDownloader"
    mkdir -p "$TOOLS/dd"
    curl -sL https://github.com/SteamRE/DepotDownloader/releases/latest/download/DepotDownloader-linux-x64.zip \
      -o "$TOOLS/dd.zip" || die "DepotDownloader download failed (no outbound net?)"
    unzip -oq "$TOOLS/dd.zip" -d "$TOOLS/dd"; chmod +x "$DD"
  fi
  ARGS=(-app "$APP" -depot "$DEPOT" -os linux -dir "/work/game/$LABEL/_depot" -validate)
  [ -n "$BRANCH" ] && ARGS+=(-branch "$BRANCH")
  [ -n "$BETAPW" ] && ARGS+=(-betapassword "$BETAPW")
  info "DepotDownloader ${ARGS[*]}"
  # run inside the SDK container so no .NET runtime is needed on the host
  sdk_run -- /work/.tools/dd/DepotDownloader "${ARGS[@]}"
  D="$DEST/_depot/valheim_server_Data/Managed"
  [ -d "$D" ] || die "depot has no valheim_server_Data/Managed — wrong depot? (server content is 896661)"
  rsync -a --delete "$D/" "$MANAGED/"
fi

# BepInEx core: unchanged by a game patch UNLESS Unity bumped. Copy whatever we were pointed at;
# if 1.0 needs a new BepInExPack, unzip it somewhere and pass --bepinex <that>/BepInEx/core.
if [ -d "$BEPINEX_SRC" ]; then
  rsync -a --delete "$BEPINEX_SRC/" "$DEST/BepInEx/core/"
  info "BepInEx core from $BEPINEX_SRC ($(ls "$DEST/BepInEx/core" | wc -l) files)"
else
  info "WARNING: no BepInEx core staged ($BEPINEX_SRC missing) — rebuild.sh will fail"
fi

ln -sfnT valheim_server_Data/Managed "$DEST/Managed"

say "staged assemblies"
ls -l "$MANAGED"/assembly_valheim.dll "$MANAGED"/assembly_utils.dll 2>/dev/null | sed 's/^/   /'
info "Unity engine: $(ls "$MANAGED"/UnityEngine.dll >/dev/null 2>&1 && md5sum "$MANAGED"/UnityEngine.CoreModule.dll | cut -c1-12 || echo n/a) (CoreModule md5 prefix — compare across labels for a Unity bump)"

say "version"
print_game_version "$LABEL"
timer_end "fetch-build $LABEL"
