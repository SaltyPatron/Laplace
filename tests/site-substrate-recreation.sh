#!/usr/bin/env bash
# A delivery recreates only a substrate database whose host declares it may (LAPLACE_SUBSTRATE_RECREATE=version), and
# never one that also holds the application schema. Runs site.sh's own recreate_substrate with the migrator stubbed.
set -euo pipefail
: "${TMPDIR:?set TMPDIR to build scratch}"
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/substrate-recreation-check.XXXXXX")
trap 'rm -rf "$work"' EXIT
sed -n '/^recreate_substrate() {/,/^}/p' "$root/deploy/linux/site.sh" > "$work/functions.sh"
grep -q '^recreate_substrate() {' "$work/functions.sh"
cat > "$work/run.sh" <<'SCRIPT'
set -euo pipefail
fail() { echo "$*" >&2; exit 1; }
say() { :; }
migrations() { printf '%s\n' "$*" >> "$WORK/migrations"; }
source "$WORK/functions.sh"
recreate_substrate 'laplace_substrate aaaa -> bbbb'
SCRIPT
export WORK="$work" LAPLACE_DBNAME=laplace-mono
run() { rm -f "$work/migrations"; env "$@" bash "$work/run.sh" > "$work/result" 2>&1; }

if run -u LAPLACE_APP_DBNAME LAPLACE_SUBSTRATE_RECREATE=version; then echo 'FAIL recreated a database that holds the application schema (no application database declared)'; exit 1; fi
[[ ! -e "$work/migrations" ]] && grep -q 'holds the application schema' "$work/result"
if run LAPLACE_APP_DBNAME=laplace-mono LAPLACE_SUBSTRATE_RECREATE=version; then echo 'FAIL recreated the application database'; exit 1; fi
[[ ! -e "$work/migrations" ]] && grep -q 'holds the application schema' "$work/result"
echo 'PASS a substrate that holds the application schema is never recreated'

if run -u LAPLACE_SUBSTRATE_RECREATE LAPLACE_APP_DBNAME=laplace-app; then echo 'FAIL recreated a substrate the host did not declare recreatable'; exit 1; fi
[[ ! -e "$work/migrations" ]] && grep -q 'LAPLACE_SUBSTRATE_RECREATE=version' "$work/result"
echo 'PASS an undeclared host refuses and names the deliberate recreation'

run LAPLACE_APP_DBNAME=laplace-app LAPLACE_SUBSTRATE_RECREATE=version
[[ "$(cat "$work/migrations")" == 'nuke --yes' ]]
echo 'PASS a declared host with its own application database recreates the substrate (nuke)'
