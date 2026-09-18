#!/usr/bin/env bash
# Does `tenet crosscheck` actually notice a damaged declaration?
#
# Crosscheck compares what Tenet's .olean reader decodes against what Lean's own exporter wrote for the same
# declarations. It is the only test that can catch a reader defect, because comparing verdicts cannot: a reader
# that drops a hypothesis yields a weaker theorem that every kernel accepts. So crosscheck carries the whole
# weight of the claim that the reader is faithful, and it has only ever been run on undamaged inputs, where it
# reports no differences. A check that has never been shown to fail is not yet evidence of anything.
#
# This damages the export one mutation kind at a time and asks whether crosscheck says so. Any kind it misses is
# a class of reader defect that would go unnoticed. Binder names and implicitness are expected to be reported as
# cosmetic rather than substantive: the kernel ignores them, so they cannot change a verdict.
#
#   tools/readercheck/readercheck.sh <export.ndjson> <Module.olean>
set -u
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export PATH=$DOTNET_ROOT:$PATH
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TENET=${TENET:-$ROOT/src/Tenet.Cli/bin/Release/net10.0/tenet}
DT=${DT:-$ROOT/tools/Tenet.DiffTest/bin/Release/net10.0/Tenet.DiffTest}
EXPORT=${1:?usage: readercheck.sh <export.ndjson> <Module.olean>}
OLEAN=${2:?usage: readercheck.sh <export.ndjson> <Module.olean>}
WORK=${WORK:-$(mktemp -d)}
SEED=${SEED:-1}
PER_KIND=${PER_KIND:-6}

# Two kinds of damage are inert by construction and must be reported without failing the run: a binder's name and
# its implicit/explicit marking are elaboration metadata the kernel ignores, and a let's nonDep flag is normalized
# to false by lean4export on purpose. Both must still be visible; being invisible is what was wrong before.
INERT=" binder-info let-nondep "

# The undamaged export is not identical to the .olean: lean4export drops binder names through its sharing table
# and normalizes the nonDep hint. Measure that first, so a mutation is judged by the change it causes.
base=$($TENET crosscheck "$EXPORT" "$OLEAN" --json 2>/dev/null | tail -1)
BASE_COS=$(echo "$base" | sed -n 's/.*"cosmetic": *\([0-9]*\).*/\1/p'); BASE_COS=${BASE_COS:-0}
BASE_NRM=$(echo "$base" | sed -n 's/.*"normalizedByExporter": *\([0-9]*\).*/\1/p'); BASE_NRM=${BASE_NRM:-0}
BASE_SUB=$(echo "$base" | sed -n 's/.*"substantive": *\([0-9]*\).*/\1/p'); BASE_SUB=${BASE_SUB:-0}
if [ "$BASE_SUB" -ne 0 ]; then
  echo "the undamaged export already shows $BASE_SUB substantive differences; fix that before reading anything below"
  exit 2
fi

echo "readercheck: $EXPORT against $OLEAN"
echo "  baseline: $BASE_COS cosmetic, $BASE_NRM normalized by the exporter, 0 substantive"
echo "  $PER_KIND mutations per kind, seed $SEED, work in $WORK"
echo

missed=0
printf "%-22s %-10s %s\n" kind verdict detail
for kind in $($DT --list-kinds | awk '{print $1}'); do
  d=$WORK/$kind
  mkdir -p $d
  $DT --export "$EXPORT" --mutate-only --variants 1 --mutations $PER_KIND \
      --kinds "$kind" --seed $SEED --out "$d" > "$d/applied.txt" 2>&1
  variant=$d/variant-000.ndjson
  if [ ! -f "$variant" ]; then
    printf "%-22s %-10s %s\n" "$kind" "skipped" "the mutator could not apply it to this export"
    continue
  fi
  applied=$(awk -F'\t' 'NF>1 {print $2}' "$d/applied.txt" | tr ',' '\n' | grep -c .)
  if [ "${applied:-0}" -eq 0 ]; then
    printf "%-22s %-10s %s\n" "$kind" "skipped" "no mutation of this kind applied"
    continue
  fi
  out=$($TENET crosscheck "$variant" "$OLEAN" --json 2>/dev/null | tail -1)
  sub=$(echo "$out" | sed -n 's/.*"substantive": *\([0-9]*\).*/\1/p')
  cos=$(echo "$out" | sed -n 's/.*"cosmetic": *\([0-9]*\).*/\1/p')
  nrm=$(echo "$out" | sed -n 's/.*"normalizedByExporter": *\([0-9]*\).*/\1/p')
  inert=0
  case "$INERT" in *" $kind "*) inert=1 ;; esac
  # A baseline count is subtracted: the undamaged export already differs from the .olean in these two buckets,
  # so "non-zero" would be true whether or not the mutation was noticed.
  cos_delta=$(( ${cos:-0} - BASE_COS ))
  nrm_delta=$(( ${nrm:-0} - BASE_NRM ))
  if [ "${sub:-0}" -gt 0 ]; then
    printf "%-22s %-10s %s\n" "$kind" "caught" "$sub substantive ($applied applied)"
  elif [ $inert -eq 1 ] && { [ $cos_delta -ne 0 ] || [ $nrm_delta -ne 0 ]; }; then
    printf "%-22s %-10s %s\n" "$kind" "inert" "visible, not substantive (cosmetic ${cos_delta:+$cos_delta} normalized ${nrm_delta:+$nrm_delta} vs baseline)"
  else
    printf "%-22s %-10s %s\n" "$kind" "MISSED" "substantive=${sub:-?} cosmetic=${cos:-?} normalized=${nrm:-?} ($applied applied)"
    missed=$((missed + 1))
  fi
done

echo
if [ $missed -gt 0 ]; then
  echo "$missed mutation kind(s) went unnoticed: each is a class of reader defect crosscheck would not report"
  exit 1
fi
echo "every mutation kind was noticed"
