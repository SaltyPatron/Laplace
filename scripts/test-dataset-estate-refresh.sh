#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

SCRIPT="$ROOT/scripts/dataset-estate-refresh.sh"
python3 scripts/test-dataset-estate-refresh.py
bash -n "$SCRIPT"

tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
DATA="$tmp/data"
REFRESH="$tmp/refresh"
mkdir -p "$DATA" "$REFRESH"

run_refresh() {
  LAPLACE_DATA_ROOT="$DATA" \
  LAPLACE_DATA_REFRESH_ROOT="$REFRESH" \
  LAPLACE_DATA_REFRESH_SOURCES="${LAPLACE_DATA_REFRESH_SOURCES:-$ROOT/scripts/dataset-estate-refresh.sources.psv}" \
    "$SCRIPT" "$@"
}

# Help/plan/status are source-only and must not touch active data.
run_refresh plan semantic >/dev/null
run_refresh status >/dev/null

# Verification must aggregate every bad artifact. A later good row cannot erase an
# earlier failure merely because the manifest walker itself returns successfully.
manifest="$tmp/synthetic.sources.psv"
printf 'semantic|missing-first|url|missing.txt|https://example.invalid/missing||||\n' > "$manifest"
printf 'semantic|good-last|url|good.txt|https://example.invalid/good||||\n' >> "$manifest"
printf 'present\n' > "$REFRESH/good.txt"
set +e
verify_output="$(LAPLACE_DATA_ROOT="$DATA" \
  LAPLACE_DATA_REFRESH_ROOT="$REFRESH" \
  LAPLACE_DATA_REFRESH_SOURCES="$manifest" \
  "$SCRIPT" verify all 2>&1)"
verify_rc=$?
set -e
[[ "$verify_rc" -ne 0 ]] || { echo "verify incorrectly accepted a missing first artifact" >&2; exit 1; }
grep -F 'missing-first' <<<"$verify_output" | grep -F 'BAD/MISSING' >/dev/null
grep -F 'good-last' <<<"$verify_output" | grep -F 'OK' >/dev/null

# A finalized-but-invalid staged artifact is evidence. A worker must fail closed,
# preserve its bytes, and leave a durable nonzero exit receipt even though die()
# exits from inside the worker call stack.
printf 'preserve-me' > "$REFRESH/finalized.bin"
set +e
LAPLACE_DATA_ROOT="$DATA" \
LAPLACE_DATA_REFRESH_ROOT="$REFRESH" \
  "$SCRIPT" _job invalid-finalized row_worker semantic invalid-finalized url \
  finalized.bin https://example.invalid/finalized '' 999999 '' '' >/dev/null 2>&1
worker_rc=$?
set -e
[[ "$worker_rc" -ne 0 ]] || { echo "invalid finalized artifact worker unexpectedly succeeded" >&2; exit 1; }
[[ "$(cat "$REFRESH/finalized.bin")" == 'preserve-me' ]] || { echo "invalid finalized artifact was overwritten" >&2; exit 1; }
[[ -f "$REFRESH/.jobs/invalid-finalized.rc" ]] || { echo "failed worker did not write exit receipt" >&2; exit 1; }
[[ "$(cat "$REFRESH/.jobs/invalid-finalized.rc")" != '0' ]] || { echo "failed worker wrote success receipt" >&2; exit 1; }

# The symmetric success path also records rc=0 without contacting the network: an
# already-present unconstrained plain artifact is a valid staged observation.
printf 'already-present\n' > "$REFRESH/already.txt"
LAPLACE_DATA_ROOT="$DATA" \
LAPLACE_DATA_REFRESH_ROOT="$REFRESH" \
  "$SCRIPT" _job valid-existing row_worker semantic valid-existing url \
  already.txt https://example.invalid/already '' '' '' '' >/dev/null 2>&1
[[ "$(cat "$REFRESH/.jobs/valid-existing.rc")" == '0' ]] || { echo "successful worker did not write rc=0" >&2; exit 1; }

# Git snapshot creation and replay exercise the set -u-sensitive sidecar path.
# The source is local so this remains deterministic and network-free.
git_source="$tmp/git-source"
mkdir -p "$git_source"
git -C "$git_source" init -q
git -C "$git_source" config user.name 'Dataset refresh test'
git -C "$git_source" config user.email 'dataset-refresh@example.invalid'
printf 'snapshot payload\n' > "$git_source/payload.txt"
git -C "$git_source" add payload.txt
git -C "$git_source" commit -qm 'fixture'
git_sha="$(git -C "$git_source" rev-parse HEAD)"
LAPLACE_DATA_ROOT="$DATA" \
LAPLACE_DATA_REFRESH_ROOT="$REFRESH" \
  "$SCRIPT" _job git-snapshot row_worker semantic git-snapshot git \
  snapshots "$git_source" "$git_sha" '' '' '' >/dev/null 2>&1
snapshot="$REFRESH/snapshots/git-snapshot-$git_sha.tar.gz"
[[ -f "$snapshot" && -f "$snapshot.sha256" ]] || { echo "Git snapshot sidecar was not created" >&2; exit 1; }
rm -f -- "$snapshot.sha256"
LAPLACE_DATA_ROOT="$DATA" \
LAPLACE_DATA_REFRESH_ROOT="$REFRESH" \
  "$SCRIPT" _job git-snapshot-replay row_worker semantic git-snapshot git \
  snapshots "$git_source" "$git_sha" '' '' '' >/dev/null 2>&1
[[ "$(cat "$REFRESH/.jobs/git-snapshot-replay.rc")" == '0' ]] || { echo "Git snapshot replay failed" >&2; exit 1; }
[[ -f "$snapshot.sha256" ]] || { echo "interrupted Git snapshot sidecar was not recovered" >&2; exit 1; }

# wait must fail closed for a job that is no longer alive but has no exit receipt.
printf '999999999\n' > "$REFRESH/.jobs/orphan.pid"
rm -f "$REFRESH/.jobs/orphan.rc"
set +e
run_refresh wait >/dev/null 2>&1
wait_rc=$?
set -e
[[ "$wait_rc" -ne 0 ]] || { echo "wait accepted ended job without exit receipt" >&2; exit 1; }

# adopt-existing inventories staged evidence while excluding its own bookkeeping and
# partial files. It must not mutate or promote anything under the active data root.
printf 'partial\n' > "$REFRESH/ignored.part"
printf 'active-sentinel\n' > "$DATA/do-not-touch"
run_refresh adopt-existing >/dev/null
grep -F 'already.txt' "$REFRESH/STAGING_LOCAL.tsv" >/dev/null
! grep -F 'ignored.part' "$REFRESH/STAGING_LOCAL.tsv" >/dev/null
[[ "$(cat "$DATA/do-not-touch")" == 'active-sentinel' ]]

echo "DATASET_ESTATE_REFRESH_SHELL_OK"
