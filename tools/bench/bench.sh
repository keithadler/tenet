#!/bin/zsh
# A committed benchmark, so a change that makes checking slower is visible instead of discovered
# by hand months later. Writes one JSON line per case; compare runs with tools/bench/compare.py.
#
#   tools/bench/bench.sh > bench-$(git rev-parse --short HEAD).json
#
# Cases use the Lean toolchain's own libraries, so they need no build beyond elan.
set -e
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export PATH=$DOTNET_ROOT:$PATH
TENET=${TENET:-$(cd "$(dirname "$0")/../.." && pwd)/src/Tenet.Cli/bin/Release/net10.0/tenet}
TOOLCHAIN=${TOOLCHAIN:-$HOME/.elan/toolchains/leanprover--lean4---v4.34.0-rc2}
LIB=$TOOLCHAIN/lib/lean

if [ ! -x "$TENET" ]; then echo "no tenet binary at $TENET (dotnet build -c Release)" >&2; exit 2; fi
if [ ! -d "$LIB" ]; then echo "no Lean toolchain at $TOOLCHAIN" >&2; exit 2; fi

run() {  # name, then the arguments
  local name=$1; shift
  local start=$(python3 -c 'import time;print(time.time())')
  "$TENET" "$@" --quiet > /dev/null 2>&1 || true
  local end=$(python3 -c 'import time;print(time.time())')
  python3 -c "
import json,sys
print(json.dumps({'case': '$name', 'seconds': round($end - $start, 2), 'args': '''$*'''}))"
}

echo "{\"commit\": \"$(git rev-parse --short HEAD 2>/dev/null || echo unknown)\", \"when\": \"$(date -u +%FT%TZ)\"}"
run "init-all-12jobs"  check "$LIB/Init.olean" --all
run "init-all-1job"    check "$LIB/Init.olean" --all --jobs 1
run "init-core"        check "$LIB/Init/Core.olean"
run "lean-all"         check "$LIB/Lean.olean" --all
