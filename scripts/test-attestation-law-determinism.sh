#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

outs=(
  engine/core/src/generated/relation_law.c
  engine/core/src/generated/pos_law.c
  engine/core/include/laplace/core/relation_law.h
  engine/core/include/laplace/core/pos_law.h
  engine/core/include/laplace/core/highway_manifest.h
  extension/laplace_substrate/sql/generated/seed_relation_types.sql.in
  extension/laplace_substrate/sql/generated/seed_pos.sql.in
)
python3 scripts/codegen-attestation-law.py
for f in "${outs[@]}"; do
  [[ -f "$f" ]] || { echo "::error file=$f::codegen did not produce a declared output"; exit 1; }
done
a=$(cat "${outs[@]}" | sha256sum | cut -d' ' -f1)
rm -f "${outs[@]}"
python3 scripts/codegen-attestation-law.py
b=$(cat "${outs[@]}" | sha256sum | cut -d' ' -f1)
[[ "$a" == "$b" ]] || { echo "::error::codegen non-deterministic ($a != $b)"; exit 1; }
echo "codegen deterministic across a clean regen: $a"
