#!/usr/bin/env bash
# The preloaded extension module loads before the execution library, so every engine
# symbol it references must be defined by the engine libraries it links. A shared
# library links with undefined symbols; PostgreSQL then refuses to start
# ("undefined symbol: laplace_consensus_scan"). This check fails the build instead.
# usage: check-preloaded-module-symbols.sh <module.so> <linked engine .so>...
set -euo pipefail
module="$1"; shift
undefined="$(nm -D --undefined-only "$module" | awk '{print $2}' |
    grep -E '^(laplace_|content_|tier_tree|trajectory_|hash128|intent_stage|codepoint_|glicko|math4d)' | sort -u || true)"
[ -n "$undefined" ] || exit 0
defined="$(for lib in "$@"; do nm -D --defined-only "$lib" | awk '{print $3}'; done | sort -u)"
missing="$(comm -23 <(printf '%s\n' "$undefined") <(printf '%s\n' "$defined"))"
if [ -n "$missing" ]; then
    echo "error: preloaded module $(basename "$module") references engine symbols no linked library defines:" >&2
    printf '  %s\n' $missing >&2
    exit 1
fi
