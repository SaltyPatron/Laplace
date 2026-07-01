#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

TEST_ONLY=0
SKIP_PROMOTE=0
FROM_SOURCE=""
SINGLE_DB=""
SINGLE_SOURCE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --test-only) TEST_ONLY=1; shift ;;
    --skip-promote) SKIP_PROMOTE=1; shift ;;
    --from) FROM_SOURCE="$2"; shift 2 ;;
    --db) SINGLE_DB="$2"; shift 2 ;;
    -*) echo "unknown flag: $1" >&2; exit 2 ;;
    *)
      if [[ -z "$SINGLE_SOURCE" ]]; then SINGLE_SOURCE="$1"; else echo "unexpected arg: $1" >&2; exit 2; fi
      shift
      ;;
  esac
done

if [[ -n "$SINGLE_DB" ]]; then
  [[ -n "$SINGLE_SOURCE" ]] || { echo "--db requires source" >&2; exit 2; }
  "$SCRIPTS/decomposer-test.sh" "$SINGLE_SOURCE" --db "$SINGLE_DB"
  if [[ "$TEST_ONLY" == "0" && "$SKIP_PROMOTE" == "0" ]]; then
    "$SCRIPTS/decomposer-promote.sh" "$SINGLE_SOURCE"
  fi
  exit 0
fi

if [[ -n "$SINGLE_SOURCE" ]]; then
  "$SCRIPTS/decomposer-test.sh" "$SINGLE_SOURCE"
  if [[ "$TEST_ONLY" == "0" && "$SKIP_PROMOTE" == "0" ]]; then
    "$SCRIPTS/decomposer-promote.sh" "$SINGLE_SOURCE"
  fi
  exit 0
fi

mapfile -t ORDER < <(python3 -c "import json; print('\n'.join(json.load(open('$SCRIPTS/decomposer-gates.json'))['manifest_order']))")

SKIP=0
[[ -n "$FROM_SOURCE" ]] && SKIP=1

for src in "${ORDER[@]}"; do
  if [[ "$SKIP" == "1" ]]; then
    if [[ "$src" == "$FROM_SOURCE" ]]; then SKIP=0; else echo "matrix skip $src (before --from $FROM_SOURCE)"; continue; fi
  fi
  echo
  echo "========================================"
  echo "==== matrix: test $src ===="
  echo "========================================"
  "$SCRIPTS/decomposer-test.sh" "$src" || { echo "matrix stopped at $src"; exit 1; }
  if [[ "$TEST_ONLY" == "0" && "$SKIP_PROMOTE" == "0" ]]; then
    "$SCRIPTS/decomposer-promote.sh" "$src" || { echo "promote failed at $src"; exit 1; }
  else
    echo "matrix: promote skipped for $src"
  fi
done

echo "DECOMPOSER-MATRIX COMPLETE"
