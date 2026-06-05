#!/usr/bin/env bash
# scripts/decomposer-ladder-ci.sh
# CI gate for the seed/lexical decomposer ladder. Ingests the bounded lexical layer on the
# current `laplace` DB (T0 already seeded by the db-deploy job) and asserts the seed
# decomposers land CONTENT (mantissa-packed physicalities), not just attestations — the
# regression the pre-fix WordNet/OMW/UD exhibited (string-keyed `word:` entities, zero content).
#
# Env:
#   LADDER_FULL=1   also ingest omw + ud (slower). The big corpora (tatoeba/conceptnet/
#                   wiktionary) are NEVER run here — they're multi-hour; for those use
#                   `just audit-decomposers --full` out of band.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"
export LAPLACE_PERFCACHE_BIN="${LAPLACE_PERFCACHE_BIN:-$ROOT/build/engine/core/perfcache/laplace_t0_perfcache.bin}"
PSQL=(psql -d laplace -U laplace_admin -v ON_ERROR_STOP=1 -tAc)

log() { echo "[ladder] $*"; }
die() { echo "::error::$*"; exit 1; }

# No checkpoint to clear: ingestion is idempotent (content-addressing + ON CONFLICT
# DO NOTHING), so a re-run after the prior job's DB nuke converges with no side journal.

# `ingest unicode` is layer 0: idempotent (ON CONFLICT) and sets HasLayerCompleted/0, which
# unblocks iso639 (layer 1). Then the lexical layer. (seed-t0/db-fresh route through the
# same command now — the marker-less legacy seed is deleted.)
LADDER=(unicode iso639 wordnet)
[[ "${LADDER_FULL:-0}" == "1" ]] && LADDER+=(omw ud)

for s in "${LADDER[@]}"; do
  echo "::group::ingest $s"
  scripts/ingest-source.sh "$s" || die "ingest $s failed"
  echo "::endgroup::"
done

# Substrate operating surface only — no hand-built hash expressions.
# Regression gate: WordNet must emit CONTENT physicalities (kind=1). Zero before the fix.
wn_content=$("${PSQL[@]}" "SELECT laplace.content_count(laplace.source_id('WordNetDecomposer'));")
log "WordNet CONTENT physicalities = ${wn_content}"
[[ "${wn_content:-0}" -gt 0 ]] || die "WordNet emitted no CONTENT physicalities — content path regressed (string-keyed / attestations-only)"

wn_att=$("${PSQL[@]}" "SELECT laplace.evidence_count(p_source => laplace.source_id('WordNetDecomposer'));")
log "WordNet attestations = ${wn_att}"
[[ "${wn_att:-0}" -gt 0 ]] || die "WordNet emitted no attestations"

# Informational: entities witnessed by >1 source (cross-source convergence) — grows once a
# 2nd content source (omw/ud/model) lands; not a hard gate in the bounded ladder.
multi=$("${PSQL[@]}" "SELECT laplace.multi_source_entity_count();")
echo "::notice::decomposer ladder OK — WordNet content=${wn_content}, attestations=${wn_att}, multi-source entities=${multi}"
