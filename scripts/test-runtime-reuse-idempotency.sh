#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/deploy/linux/payload-sync.sh"

TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT
REFERENCE="$TEST_ROOT/reference"
DESTINATION="$TEST_ROOT/destination"
printf 'same-runtime-bytes\n' > "$REFERENCE"
ln "$REFERENCE" "$DESTINATION"

before="$(stat -c '%d:%i' "$REFERENCE")"
[[ "$(stat -c '%d:%i' "$DESTINATION")" == "$before" ]]

laplace_reuse_runtime_file "$REFERENCE" "$DESTINATION"

[[ -f "$DESTINATION" ]]
[[ "$(stat -c '%d:%i' "$DESTINATION")" == "$before" ]] || {
  echo "already-reused destination was unnecessarily replaced" >&2
  exit 1
}
[[ ! -e "$DESTINATION.reuse" ]] || {
  echo "already-reused destination leaked a temporary hardlink" >&2
  exit 1
}

echo "RUNTIME_REUSE_IDEMPOTENCY_OK inode=$before"
