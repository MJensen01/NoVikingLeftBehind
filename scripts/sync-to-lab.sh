#!/usr/bin/env bash
# Push this repo's src/ tree to the box lab (shroom-pi:/opt/modlab/src/NoVikingLeftBehind/),
# which stays the build+test environment (see research/BUILD-LAB.md in the old combined repo)
# until CI is proven out end-to-end.
#
# Because src/Directory.Build.props is the nearest such file to the .csproj, once synced it
# is what the lab's own ./build.sh actually builds against (MSBuild uses the nearest
# Directory.Build.props found walking up from the project, not a merge of every one found) —
# NOT the lab's separate top-level /opt/modlab/Directory.Build.props. That's deliberate: this
# repo's props file is self-sufficient (defaults to /game/... exactly like the lab's own file
# did, so the lab's build.sh needs no changes) and also works standalone via
# VALHEIM_MANAGED/VALHEIM_BEPINEX_CORE for a non-lab machine. See src/Directory.Build.props.
#
# Usage: scripts/sync-to-lab.sh [remote-host] [remote-path]
set -euo pipefail

REMOTE_HOST="${1:-shroom-pi}"
REMOTE_PATH="${2:-/opt/modlab/src/NoVikingLeftBehind}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Syncing $REPO_ROOT/src/ -> $REMOTE_HOST:$REMOTE_PATH/"

# --delete keeps the lab's copy exactly matching the repo (minus build output, which the lab
# itself regenerates under bin/obj on every build). Exclude vendored bin/obj defensively too,
# in case a local build was run before syncing.
rsync -avz --delete \
  --exclude 'bin/' \
  --exclude 'obj/' \
  "$REPO_ROOT/src/" "$REMOTE_HOST:$REMOTE_PATH/"

echo "Done. On $REMOTE_HOST: cd /opt/modlab && ./build.sh src/NoVikingLeftBehind"
