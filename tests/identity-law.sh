#!/usr/bin/env bash
# The identity law on the monorepo's composition code (engine/core/src/hash128.c) as the build compiles it: with
# Laplace-Native's lp_id_compose (src/identity.c) and BLAKE3. The vectors are IdentityLawVectorTests' (one list).
#   tests/identity-law.sh <Laplace-Native checkout> <BLAKE3 c/ directory>
# Composition needs no ROM. Label/text vectors also run against the candidate core
# and its generated scalar floor in laplace_identity_law_vectors (CTest).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
native="${1:?Laplace-Native checkout}"; blake3="${2:?BLAKE3 c/ directory}"
cs="$root/app/Laplace.Core.Tests/Core/IdentityLawVectorTests.cs"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
core="$root/engine/core"
"${CC:-cc}" -std=c17 -O2 -Wall -Werror=implicit-function-declaration -ffunction-sections -fdata-sections \
  -DBLAKE3_NO_SSE2 -DBLAKE3_NO_SSE41 -DBLAKE3_NO_AVX2 -DBLAKE3_NO_AVX512 \
  -I"$core/include" -I"$native/include" -I"$native/src" -I"$blake3" \
  "$root/tests/identity-law/vectors.c" \
  "$core/src/hash128.c" "$core/src/mantissa.c" "$core/src/trajectory.c" \
  "$native/src/identity.c" "$native/src/utf8.c" \
  "$blake3/blake3.c" "$blake3/blake3_dispatch.c" "$blake3/blake3_portable.c" \
  -Wl,--gc-sections -o "$work/identity-law"
sed -n 's/^ *\[InlineData("\([^"]*\)", "\([0-9a-f]\{32\}\)")\].*/\1\t\2/p' "$cs" > "$work/vectors.tsv"
[[ "$(wc -l < "$work/vectors.tsv")" -ge 7 ]] || { echo "::error::fewer than 7 vectors read from $cs" >&2; exit 1; }
"$work/identity-law" < "$work/vectors.tsv"
