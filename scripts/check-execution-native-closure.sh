#!/usr/bin/env bash
# A versioned query image must not import its algorithms from the older
# shared core already mapped by a running PostgreSQL postmaster.
set -euo pipefail
module=${1:?execution module required}
dynamic=$(readelf -d "$module")
if [[ "$dynamic" =~ liblaplace_(core|dynamics) ]]; then
    echo 'execution module still depends on a shared core or dynamics image' >&2
    exit 1
fi
imports=$(nm -D --undefined-only "$module")
if [[ "$imports" =~ [[:space:]](lp_|tier_tree_|native_text_|text_identity_)[[:alnum:]_]+ ]]; then
    echo 'execution module still imports Native text or tree algorithms' >&2
    exit 1
fi
echo 'Execution Native closure is module-local; no shared core or Native text imports.'
