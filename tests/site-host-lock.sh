#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?build scratch required}"
# Exercise the interactive default even when CI itself is configured to wait.
unset LAPLACE_HOST_LOCK_WAIT_SECONDS
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/site-host-lock.XXXXXX")
trap 'rm -rf "$work"' EXIT
sed -n '/^host_lock() {/,/^}/p' "$root/deploy/linux/site.sh" > "$work/functions"
cat "$root/deploy/linux/host-lock.sh" >> "$work/functions"
export LAPLACE_LOCKS=$work FUNCTIONS=$work/functions
fail(){ echo "$*" >&2; exit 1; }
source "$FUNCTIONS"
# An operator's restrictive umask must not create a lock that excludes the runner.
original_mask=$(umask)
umask 077
host_lock_hold exclusive 8 "$work/restrictive.lock" 0
[[ $(stat -c %a "$work/restrictive.lock") == 664 ]]
[[ $(stat -c %a "$work/restrictive.lock.turnstile") == 664 ]]
[[ $(umask) == 0077 ]]
exec 8>&-
umask "$original_mask"
echo 'PASS restrictive caller umask does not exclude other lock users'
# An open error is a permission/path error, never a contention deadline.
rc=0
host_lock_hold exclusive 8 "$work/missing/host.lock" 3600 >"$work/open-error" 2>&1 || rc=$?
[[ $rc == 1 ]]
grep -q 'cannot open host lock:' "$work/open-error"
if grep -q 'deadline expired' "$work/open-error"; then echo 'open error reported as contention'; exit 1; fi
echo 'PASS failed lock open returns immediately with the actual error'
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

# Writer preference (#1899): while a deployment waits for an ingest, a later ingest waits behind it, not past it; the
# ingest's own nested command, which inherited its lock, still goes on at once.
host_lock shared
LAPLACE_HOST_LOCK_WAIT_SECONDS=wait bash -c 'fail(){ exit 1; }; source "$FUNCTIONS"; host_lock exclusive && touch "$LAPLACE_LOCKS/deployed"; sleep 1' 8>&- 2> "$work/deploy-waiting" &
deployment=$!
for attempt in {1..100}; do grep -q 'Waiting' "$work/deploy-waiting" && break; sleep .02; done
grep -q 'Waiting' "$work/deploy-waiting" || { echo 'the deployment did not wait'; exit 1; }
if bash -c 'fail(){ exit 1; }; source "$FUNCTIONS"; host_lock shared' 8>&- > /dev/null 2>&1; then echo 'a later ingest overtook a waiting deployment'; exit 1; fi
LAPLACE_HOST_LOCK_WAIT_SECONDS=10 bash -c 'fail(){ exit 1; }; source "$FUNCTIONS"; host_lock shared && [[ -e "$LAPLACE_LOCKS/deployed" ]] && touch "$LAPLACE_LOCKS/after"' 8>&- 2>/dev/null &
later=$!
sleep .3
[[ ! -e "$work/deployed" ]] || { echo 'the deployment ran during the ingest'; exit 1; }
bash -c 'fail(){ exit 1; }; source "$FUNCTIONS"; host_lock shared' || { echo 'the ingest'"'"'s nested command queued behind the deployment'; exit 1; }
exec 8>&-
wait "$deployment"; wait "$later"
[[ -e "$work/deployed" && -e "$work/after" ]] || { echo 'the deployment or the later ingest did not run in turn'; exit 1; }
echo 'PASS a waiting deployment is not overtaken by a later ingest, which runs after it; a nested command does not queue'

# A compiler daemon remains alive after its caller exits. It must not keep the
# caller's deployment lock; otherwise the next workflow step fails against itself.
sed -n '/^dotnet_app() /p' "$root/deploy/linux/site.sh" >> "$work/functions"
bash -c '
  fail(){ exit 1; }; source "$FUNCTIONS"; host_lock
  dotnet(){
    bash -c '\''echo "$$" > "$LAPLACE_LOCKS/daemon.pid"; : > "$LAPLACE_LOCKS/daemon.ready"; exec sleep 30'\'' </dev/null >/dev/null 2>&1 &
  }
  dotnet_app build
  if flock -xn "$LAPLACE_LOCKS/host-resource.lock" true; then exit 1; fi
'
for attempt in {1..100}; do [[ -f "$work/daemon.ready" ]] && break; sleep .01; done
[[ -f "$work/daemon.ready" ]] || { echo 'compiler fixture did not start'; exit 1; }
daemon=$(cat "$work/daemon.pid")
trap 'kill "$daemon" 2>/dev/null || true; rm -rf "$work"' EXIT
kill -0 "$daemon"
flock -xn "$work/host-resource.lock" true
echo 'PASS compiler daemon outlives build without retaining deployment lock'
