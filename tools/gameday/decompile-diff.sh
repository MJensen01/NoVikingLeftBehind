#!/usr/bin/env bash
# gameday step 2 — decompile both staged builds and diff ONLY the game types our patches touch.
#
#   decompile-diff.sh <oldLabel> <newLabel> [--src <dir>]... [--types <t1,t2,...>] [--all]
#
# Type discovery: greps the mod sources for HarmonyPatch / AccessTools / typeof() targets and keeps
# whatever actually resolves to a type in the game assemblies. --all diffs every game type instead.
#
# Output: /opt/modlab/game/_diff/<old>_vs_<new>/
#            <asm>/old/*.cs   <asm>/new/*.cs      full ilspycmd project dumps (cached, reused)
#            hunks/<Type>.diff                     per-type unified diff
#            TABLE.md                               the per-type changed/unchanged table
set -euo pipefail
cd "$(dirname "$0")"; . ./_common.sh

OLD="${1:-}"; NEW="${2:-}"; shift 2 2>/dev/null || die "usage: decompile-diff.sh <oldLabel> <newLabel> [--src dir] [--types a,b] [--all]"
SRCS=(); TYPES_ARG=""; ALL=0
while [ $# -gt 0 ]; do
  case "$1" in
    --src)   SRCS+=("$2"); shift 2;;
    --types) TYPES_ARG="$2"; shift 2;;
    --all)   ALL=1; shift;;
    *) die "unknown arg: $1";;
  esac
done
[ ${#SRCS[@]} -gt 0 ] || SRCS=("$LAB/src/NoVikingLeftBehind" "$LAB/src/SmoothServer")

for L in "$OLD" "$NEW"; do
  [ -d "$(managed_dir "$L")" ] || die "label '$L' not staged — run fetch-build.sh $L ... first"
done
ensure_ilspy
timer_start

OUT="$GAMEROOT/_diff/${OLD}_vs_${NEW}"; guard_write "$OUT"
mkdir -p "$OUT/hunks"

say "decompile-diff: $OLD -> $NEW"

# ---------- 1. decompile each game assembly, both labels (cached) ----------
for L in "$OLD" "$NEW"; do
  for A in $GAME_ASMS; do
    D="$GAMEROOT/_dump/$L/$A"
    [ -f "$(managed_dir "$L")/$A.dll" ] || continue
    if [ -d "$D" ] && [ -n "$(ls -A "$D" 2>/dev/null)" ]; then continue; fi
    mkdir -p "$D"
    info "decompiling $L/$A.dll"
    sdk_run -- /work/.tools/bin/ilspycmd -p -o "/work/game/_dump/$L/$A" \
        "/work/game/$L/valheim_server_Data/Managed/$A.dll" >/dev/null 2>&1 \
      || info "  (ilspycmd reported problems on $A — continuing)"
  done
done

# ---------- 2. work out which types to compare ----------
TYPEFILE="$OUT/types.txt"
if [ -n "$TYPES_ARG" ]; then
  tr ',' '\n' <<<"$TYPES_ARG" | sed '/^$/d' > "$TYPEFILE"
elif [ "$ALL" = 1 ]; then
  for A in $GAME_ASMS; do
    [ -d "$GAMEROOT/_dump/$NEW/$A" ] && find "$GAMEROOT/_dump/$NEW/$A" -name '*.cs' -printf '%f\n' | sed 's/\.cs$//'
  done | sort -u > "$TYPEFILE"
else
  set +e   # every grep below legitimately finds nothing in one repo or the other
  {
    grep -rhoE '\btypeof\([A-Z][A-Za-z0-9_]*' "${SRCS[@]}" --include='*.cs' 2>/dev/null | sed 's/^typeof(//'
    grep -rhoE '\bAccessTools\.[A-Za-z]+\("[A-Za-z0-9_.]+"' "${SRCS[@]}" --include='*.cs' 2>/dev/null \
      | grep -oE '"[A-Za-z0-9_.]+"' | tr -d '"' | sed 's/\.[A-Za-z0-9_]*$//'
    grep -rhoE '\bHarmonyPatch\("[A-Za-z0-9_.]+"' "${SRCS[@]}" --include='*.cs' 2>/dev/null \
      | grep -oE '"[A-Za-z0-9_.]+"' | tr -d '"' | sed 's/\.[A-Za-z0-9_]*$//'
    # nested types referenced as Outer.Inner -> keep the outer
    grep -rhoE '\btypeof\([A-Z][A-Za-z0-9_]*\.[A-Za-z0-9_]+\)' "${SRCS[@]}" --include='*.cs' 2>/dev/null \
      | sed 's/^typeof(//; s/\..*//'
    tr ' ' '\n' <<<"$ALWAYS_TYPES"
  } | sed 's/[^A-Za-z0-9_].*$//' | sort -u | sed '/^$/d' > "$TYPEFILE.raw"
  # keep only names that exist as a decompiled type in the NEW dump (drops int/bool/our own modules)
  : > "$TYPEFILE"
  while read -r T; do
    for A in $GAME_ASMS; do
      if [ -f "$GAMEROOT/_dump/$NEW/$A/$T.cs" ] || [ -f "$GAMEROOT/_dump/$OLD/$A/$T.cs" ]; then
        echo "$T" >> "$TYPEFILE"; break
      fi
    done
  done < "$TYPEFILE.raw"
  sort -u -o "$TYPEFILE" "$TYPEFILE"
  set -e
fi
info "$(wc -l < "$TYPEFILE") game types touched by our patches"

# ---------- 3. diff ----------
TABLE="$OUT/TABLE.md"
{ echo "| type | assembly | status | +/- lines |"; echo "|---|---|---|---|"; } > "$TABLE"
CHANGED=(); GONE=()
while read -r T; do
  ASM=""; OF=""; NF=""
  for A in $GAME_ASMS; do
    [ -f "$GAMEROOT/_dump/$NEW/$A/$T.cs" ] && { ASM=$A; NF="$GAMEROOT/_dump/$NEW/$A/$T.cs"; }
    [ -f "$GAMEROOT/_dump/$OLD/$A/$T.cs" ] && { ASM=${ASM:-$A}; OF="$GAMEROOT/_dump/$OLD/$A/$T.cs"; }
    [ -n "$NF" ] && break
  done
  if [ -z "$NF" ]; then
    echo "| \`$T\` | ${ASM:-?} | **GONE (removed/renamed)** | - |" >> "$TABLE"; GONE+=("$T"); continue
  fi
  if [ -z "$OF" ]; then
    echo "| \`$T\` | $ASM | NEW (absent in $OLD) | - |" >> "$TABLE"; continue
  fi
  if diff -q "$OF" "$NF" >/dev/null; then
    echo "| \`$T\` | $ASM | unchanged | 0 |" >> "$TABLE"
  else
    diff -u "$OF" "$NF" > "$OUT/hunks/$T.diff" || true
    P=$(grep -c '^+[^+]' "$OUT/hunks/$T.diff" || true); M=$(grep -c '^-[^-]' "$OUT/hunks/$T.diff" || true)
    echo "| \`$T\` | $ASM | **CHANGED** | +$P/-$M |" >> "$TABLE"
    CHANGED+=("$T")
  fi
done < "$TYPEFILE"

say "per-type table  ($TABLE)"
cat "$TABLE"

if [ ${#CHANGED[@]} -gt 0 ]; then
  say "diff hunks for ${#CHANGED[@]} changed type(s)  (full files in $OUT/hunks/)"
  for T in "${CHANGED[@]}"; do
    echo; echo "----- $T -----"
    grep -E '^[-+][^-+]' "$OUT/hunks/$T.diff" | head -"${HUNK_LINES:-40}"
    L=$(grep -cE '^[-+][^-+]' "$OUT/hunks/$T.diff" || true)
    [ "$L" -gt "${HUNK_LINES:-40}" ] && echo "  ... ($L changed lines total, see $OUT/hunks/$T.diff)" || true
  done
fi
if [ ${#GONE[@]} -gt 0 ]; then say "TYPES REMOVED IN $NEW"; printf '   %s\n' "${GONE[@]}"; fi

# ---------- 4. bonus: Unity/BepInEx surface check ----------
say "assembly inventory delta"
diff <(ls "$(managed_dir "$OLD")") <(ls "$(managed_dir "$NEW")") | sed 's/^/   /' || true

timer_end "decompile-diff $OLD -> $NEW"
