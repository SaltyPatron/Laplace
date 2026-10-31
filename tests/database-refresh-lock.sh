#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/.." && pwd)
: "${TMPDIR:?test scratch required}"
work=$(mktemp -d "$TMPDIR/database-refresh.XXXXXXXX")
waiter=
cleanup() {
  if [[ -n $waiter ]]; then kill "$waiter" 2>/dev/null || true; wait "$waiter" 2>/dev/null || true; fi
  rm -rf -- "$work"
}
trap cleanup EXIT
installed="$work/installed helper"
mkdir "$installed"
cp "$root/deploy/linux/runtime/database-refresh.sh" "$installed/database-refresh"
cp "$root/deploy/linux/host-lock.sh" "$installed/host-lock.sh"
lock="$work/host resource.lock"
export REFRESH_TEST_WORK=$work REFRESH_TEST_LOCK=$lock
cat > "$work/credential check" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ $LAPLACE_HOST_LOCK_FD == 8 && /proc/self/fd/8 -ef $REFRESH_TEST_LOCK ]]
flock -x -n 8
[[ $# == 1 && $1 == 'argument with spaces' ]]
if flock -x -n "$REFRESH_TEST_LOCK" true; then
  echo 'Credential command did not retain the exclusive lock' >&2; exit 1
fi
touch "$REFRESH_TEST_WORK/credential-entered"
exit "${REFRESH_TEST_RESULT:-0}"
SH
source "$root/deploy/linux/host-lock.sh"
host_lock_hold exclusive 8 "$lock" 0
# Represent an independent timer, with no inherited deployment descriptor.
env -u LAPLACE_HOST_LOCK_WAIT_SECONDS bash "$installed/database-refresh" "$lock" \
  bash "$work/credential check" 'argument with spaces' 8>&- 2>"$work/waiting" &
waiter=$!
for ((attempt=0; attempt<100; attempt++)); do
  if rg -q '^Waiting ' "$work/waiting"; then break; fi
  sleep .02
done
rg -q '^Waiting ' "$work/waiting"
kill -0 "$waiter"
[[ ! -e $work/credential-entered ]]
# Later readers must not overtake the queued credential writer.
status=0
bash -c 'source "$1"; host_lock_hold shared 8 "$2" 0' _ \
  "$installed/host-lock.sh" "$lock" 8>&- 2>"$work/reader" || status=$?
[[ $status == 3 ]]
flock -u 8
wait "$waiter"
waiter=
[[ -f $work/credential-entered ]]
echo 'PASS timer queues behind deployment, blocks later readers and verifies after release'

rm "$work/credential-entered"
host_lock_hold exclusive 8 "$lock" 0
bash "$installed/database-refresh" "$lock" bash "$work/credential check" 'argument with spaces'
[[ -f $work/credential-entered ]]
echo 'PASS nested verification inherits the held lock without waiting on its own owner'

rm "$work/credential-entered"
status=0
LAPLACE_HOST_LOCK_WAIT_SECONDS=0 bash "$installed/database-refresh" "$lock" \
  bash "$work/credential check" 'argument with spaces' 8>&- 2>"$work/deadline" || status=$?
[[ $status == 3 && ! -e $work/credential-entered ]]
flock -u 8
echo 'PASS explicit deadline refuses before credential access'

status=0
REFRESH_TEST_RESULT=23 bash "$installed/database-refresh" "$lock" \
  bash "$work/credential check" 'argument with spaces' 8>&- || status=$?
[[ $status == 23 ]]
flock -x -n "$lock" true
echo 'PASS credential failure remains visible and releases the queue lock'

mv "$installed/host-lock.sh" "$installed/missing-helper"
status=0
bash "$installed/database-refresh" "$lock" touch "$work/unprotected" 8>&- 2>"$work/missing" || status=$?
[[ $status != 0 && ! -e $work/unprotected ]]
echo 'PASS missing installed lock owner refuses credential work'
