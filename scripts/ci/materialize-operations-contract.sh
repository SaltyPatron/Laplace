#!/usr/bin/env bash
# Materialize the immutable build inputs without changing the host's checkout.
set -euo pipefail
source=${1:?Operations source repository required}
output=${2:?Build contract output directory required}
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source=$(cd "$source" && pwd -P)
manifest="$root/scripts/ci/operations-build-contract.tsv"
revision=$(sed -n 's/^# Git blob identities from SaltyPatron\/Laplace-Operations \([0-9a-f]\{40\}\)\.$/\1/p' "$manifest")
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo 'Operations contract has no unique revision pin' >&2; exit 1; }
# Acquire immutable objects only; leave the host branch, index and edits intact.
if ! git -c "safe.directory=$source" -C "$source" cat-file -e "$revision^{commit}" 2>/dev/null; then
  git -c "safe.directory=$source" -C "$source" fetch --no-tags --no-write-fetch-head origin "$revision"
fi
while read -r blob path; do
  [[ -z "$blob" || "$blob" == \#* ]] && continue
  destination="$output/$path"
  mkdir -p "$(dirname "$destination")"
  git -c "safe.directory=$source" -C "$source" cat-file blob "$blob" > "$destination"
  actual=$(git hash-object --no-filters "$destination")
  [[ "$actual" == "$blob" ]] || { echo "Operations contract differs from its pin: $path" >&2; exit 1; }
done < "$manifest"
