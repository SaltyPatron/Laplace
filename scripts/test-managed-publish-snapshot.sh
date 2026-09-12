#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Sourcing exposes snapshot_application_payload without running host reconciliation.
source "$ROOT/deploy/linux/managed-publish.sh"

TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT
SOURCE="$TEST_ROOT/source"
DESTINATION="$TEST_ROOT/destination"
mkdir "$SOURCE" "$DESTINATION"
printf 'shared\n' > "$SOURCE/shared.bin"
printf 'protected\n' > "$SOURCE/protected.bin"

source_shared_inode="$(stat -c '%d:%i' "$SOURCE/shared.bin")"
rsync_calls=0

# Reproduce rsync's production failure mode deterministically: the first link-dest
# pass materializes an ordinary unchanged file, cannot materialize another file,
# and returns code 23. The second pass must complete the missing file without
# replacing the already-created hardlink.
rsync() {
  rsync_calls=$((rsync_calls + 1))
  if [[ "$rsync_calls" -eq 1 ]]; then
    ln "$SOURCE/shared.bin" "$DESTINATION/shared.bin"
    return 23
  fi
  command rsync "$@"
}

snapshot_application_payload "$SOURCE" "$DESTINATION"

[[ "$rsync_calls" -eq 2 ]] || {
  echo "expected hardlink pass plus fallback copy; calls=$rsync_calls" >&2
  exit 1
}
cmp -s "$SOURCE/shared.bin" "$DESTINATION/shared.bin"
cmp -s "$SOURCE/protected.bin" "$DESTINATION/protected.bin"
[[ "$(stat -c '%d:%i' "$DESTINATION/shared.bin")" == "$source_shared_inode" ]] || {
  echo "fallback copy discarded a hardlink already created by the first pass" >&2
  exit 1
}
[[ "$(stat -c '%d:%i' "$DESTINATION/protected.bin")" != \
   "$(stat -c '%d:%i' "$SOURCE/protected.bin")" ]] || {
  echo "fallback did not materialize the denied entry as a private copy" >&2
  exit 1
}

echo "MANAGED_PUBLISH_SNAPSHOT_FALLBACK_OK hardlink_prefix=preserved denied_entry=private_copy"
