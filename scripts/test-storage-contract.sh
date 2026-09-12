#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=scripts/lib/storage.sh
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init
test_root=$(mktemp -d "$TMPDIR/storage-contract.XXXXXXXX")
trap 'rm -rf -- "$test_root"' EXIT
chmod 2770 "$test_root"
printf 'shared\n' > "$test_root/output"
[[ $(stat -c %G "$test_root/output") == laplace-runner ]]
[[ $(stat -c %a "$test_root/output") == 664 ]]
mkdir "$test_root/child"
[[ -g "$test_root/child" ]]
if (ROOT=/tmp/forbidden-checkout; laplace_storage_init) 2>/dev/null; then
    echo 'temporary checkout was accepted' >&2; exit 1
fi
if (LAPLACE_SCRATCH_ROOT=/tmp/forbidden-scratch; laplace_storage_init) 2>/dev/null; then
    echo 'temporary scratch was accepted' >&2; exit 1
fi
ln -s /tmp "$test_root/escape"
if (LAPLACE_SCRATCH_ROOT="$test_root/escape/scratch"; laplace_storage_init) 2>/dev/null; then
    echo 'scratch symlink escaped the build volume' >&2; exit 1
fi
if bash "$ROOT/scripts/agent-worktree.sh" ../escape >/dev/null 2>&1; then
    echo 'worktree name escaped its root' >&2; exit 1
fi
echo 'PASS: shared group/modes, setgid inheritance, and temporary-path rejection'
