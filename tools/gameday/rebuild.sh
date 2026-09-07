#!/usr/bin/env bash
# gameday step 3 — build both mods against a staged game build and report compile errors per project.
#
#   rebuild.sh <label> [ProjectDir ...]      # default projects: NoVikingLeftBehind SmoothServer
#
# Uses the repos' own src/Directory.Build.props override hooks (VALHEIM_MANAGED /
# VALHEIM_BEPINEX_CORE env vars -> ValheimManagedDir / BepInExCoreDir), so nothing in the repo
# has to be edited to point at a different game build.
#
# Output: /opt/modlab/game/<label>/build-<Project>.log  and a summary table.
set -euo pipefail
cd "$(dirname "$0")"; . ./_common.sh

LABEL="${1:-}"; shift || true
[ -n "$LABEL" ] || die "usage: rebuild.sh <label> [ProjectDir ...]"
[ -d "$(managed_dir "$LABEL")" ] || die "label '$LABEL' not staged — run fetch-build.sh first"
[ -d "$(label_dir "$LABEL")/BepInEx/core" ] || die "no BepInEx/core staged under $(label_dir "$LABEL")"

PROJS=("$@"); [ ${#PROJS[@]} -gt 0 ] || PROJS=(NoVikingLeftBehind SmoothServer)
timer_start
say "rebuild against '$LABEL'  ($(cat "$(label_dir "$LABEL")/VERSION" 2>/dev/null || echo 'version unknown'))"

declare -A STATUS ERRC
for P in "${PROJS[@]}"; do
  D="$LAB/src/$P"; [ -d "$D" ] || { STATUS[$P]="NO SOURCE"; ERRC[$P]=0; continue; }
  LOG="$(label_dir "$LABEL")/build-$P.log"; guard_write "$LOG"
  info "building src/$P ..."
  # Delete the previous artifact and force a full rebuild: MSBuild's up-to-date check keys off
  # source timestamps, not the reference assemblies, so an incremental build against a NEW game
  # build can silently reuse the OLD compile and report a false green.
  rm -f "$D/bin/$P.dll"
  set +e
  sdk_run -- env \
      VALHEIM_MANAGED="/work/game/$LABEL/valheim_server_Data/Managed" \
      VALHEIM_BEPINEX_CORE="/work/game/$LABEL/BepInEx/core" \
      dotnet build "/work/src/$P" -c Release -v minimal --nologo --no-incremental -t:Rebuild > "$LOG" 2>&1
  RC=$?
  set -e
  N=$(grep -cE ': error [A-Z]+[0-9]+' "$LOG" || true)
  ERRC[$P]=$N
  if [ $RC -eq 0 ]; then STATUS[$P]="OK"; else STATUS[$P]="FAILED"; fi
done

say "compile summary"
printf '| project | result | errors | log |\n|---|---|---|---|\n'
for P in "${PROJS[@]}"; do
  printf '| %s | %s | %s | game/%s/build-%s.log |\n' "$P" "${STATUS[$P]}" "${ERRC[$P]:-0}" "$LABEL" "$P"
done

for P in "${PROJS[@]}"; do
  LOG="$(label_dir "$LABEL")/build-$P.log"; [ -f "$LOG" ] || continue
  [ "${ERRC[$P]:-0}" = 0 ] && continue
  say "$P — distinct errors (${ERRC[$P]} total)"
  grep -oE ': error [A-Z]+[0-9]+:.*' "$LOG" | sed 's/^: //' | sort | uniq -c | sort -rn | head -30 | sed 's/^/   /'
  say "$P — first 15 error sites"
  grep -E ': error [A-Z]+[0-9]+' "$LOG" | sed 's#^/work/##' | head -15 | sed 's/^/   /'
done

say "artifacts"
for P in "${PROJS[@]}"; do ls -l "$LAB/src/$P/bin/$P.dll" 2>/dev/null | sed 's/^/   /' || info "$P: no DLL produced"; done
timer_end "rebuild $LABEL"
