#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?build scratch required}"
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/site-host-lock.XXXXXX")
trap 'rm -rf "$work"' EXIT
sed -n '/^host_lock() {/,/^}/p' "$root/deploy/linux/site.sh" > "$work/functions"
export LAPLACE_LOCKS=$work FUNCTIONS=$work/functions
fail(){ echo "$*" >&2; exit 1; }
source "$FUNCTIONS"
exec 9>>"$work/host-resource.lock"
flock -s 9
if bash -c 'fail(){ exit 1; }; source "$FUNCTIONS"; host_lock' 9>&- > "$work/result" 2>&1; then echo 'accepted deployment during ingest'; exit 1; fi
echo 'PASS active ingest blocks monorepo deployment'
host_lock shared
exec 8>&-
flock -u 9
host_lock exclusive
if flock -sn "$work/host-resource.lock" true; then echo 'accepted ingest during deployment'; exit 1; fi
echo 'PASS monorepo deployment blocks a new ingest'
bash -c 'fail(){ exit 1; }; source "$FUNCTIONS"; host_lock'
echo 'PASS nested commands reuse the inherited operation lock'
exec 8>&-
flock -xn "$work/host-resource.lock" true
echo 'PASS the next deployment acquires the released lock'
