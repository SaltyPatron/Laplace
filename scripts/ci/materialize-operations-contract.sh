#!/usr/bin/env bash
# Materialize the immutable build inputs without changing the host's checkout.
set -euo pipefail
source=${1:?Operations source repository required}
output=${2:?Build contract output directory required}
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
while read -r blob path; do
  [[ -z "$blob" || "$blob" == \#* ]] && continue
  destination="$output/$path"
  mkdir -p "$(dirname "$destination")"
  git -c "safe.directory=$source" -C "$source" cat-file blob "$blob" > "$destination"
  actual=$(git hash-object --no-filters "$destination")
  [[ "$actual" == "$blob" ]] || { echo "Operations contract differs from its pin: $path" >&2; exit 1; }
done < "$root/scripts/ci/operations-build-contract.tsv"
