#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

mapfile -d '' -t policy_files < <(find app \( -path 'app/Laplace.Decomposers/*.cs' -o -path 'app/Laplace.Decomposers/*/*.cs' -o -path 'app/Laplace.Substrate/Abstractions/*.cs' \) \
  ! -path 'app/Laplace.Decomposers.Tests/*' -print0)
violations=0
for pattern in AttestationFactory 'RelationTypeRegistry\.Attest' 'ScoreFp1e9\s*=' 'AttestationOutcome\.(Confirm|Refute|Draw)'; do
  while IFS= read -r file; do
    [[ -z "$file" ]] && continue
    echo "::error file=$file::Forbidden attestation policy: $pattern"
    violations=$((violations + 1))
  done < <(rg -l "$pattern" "${policy_files[@]}" 2>/dev/null || true)
done
[[ "$violations" -eq 0 ]]
