#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"
export LAPLACE_PERFCACHE_BIN="${LAPLACE_PERFCACHE_BIN:-$ROOT/build/engine/core/perfcache/laplace_t0_perfcache.bin}"
PSQL=(psql -d laplace -U laplace_admin -v ON_ERROR_STOP=1 -tAc)

log() { echo "[ladder] $*"; }
die() { echo "::error::$*"; exit 1; }

KNOWLEDGE=(wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary)
LADDER=(unicode iso639 "${KNOWLEDGE[0]}")
[[ "${LADDER_FULL:-0}" == "1" ]] && LADDER+=("${KNOWLEDGE[@]:1}")

for s in "${LADDER[@]}"; do
  echo "::group::ingest $s"
  scripts/ingest-source.sh "$s" || die "ingest $s failed"
  echo "::endgroup::"
done

wn_content=$("${PSQL[@]}" "SELECT laplace.content_count(laplace.source_id('WordNetDecomposer'));")
log "WordNet CONTENT physicalities = ${wn_content}"
[[ "${wn_content:-0}" -gt 0 ]] || die "WordNet emitted no CONTENT physicalities — content path regressed (string-keyed / attestations-only)"

wn_att=$("${PSQL[@]}" "SELECT laplace.evidence_count(p_source => laplace.source_id('WordNetDecomposer'));")
log "WordNet attestations = ${wn_att}"
[[ "${wn_att:-0}" -gt 0 ]] || die "WordNet emitted no attestations"

multi=$("${PSQL[@]}" "SELECT laplace.multi_source_entity_count();")
echo "::notice::decomposer ladder OK — WordNet content=${wn_content}, attestations=${wn_att}, multi-source entities=${multi}"
