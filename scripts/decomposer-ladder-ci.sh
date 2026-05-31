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

# Clear any stale resume checkpoint — it persists on disk across DB resets (the shared
# `laplace` DB is nuked by the prior run's model-roundtrip job), and a stale journal makes
# the runner treat already-journaled intents as applied and SKIP re-emitting them → their
# entities never land in the fresh DB and the content assertion fails. Same hazard
# model-roundtrip-ci.sh + `just db-fresh` guard against (see [[checkpoint-survives-db-nuke]]).
rm -rf /tmp/laplace-ingest 2>/dev/null || true
mkdir -p /tmp/laplace-ingest

# `ingest unicode` sets HasLayerCompleted/0 (the db-deploy seed-unicode step does NOT), which
# unblocks iso639 (layer 1). The re-seed is idempotent (ON CONFLICT). Then the lexical layer.
LADDER=(unicode iso639 wordnet)
[[ "${LADDER_FULL:-0}" == "1" ]] && LADDER+=(omw ud)

for s in "${LADDER[@]}"; do
  echo "::group::ingest $s"
  scripts/ingest-source.sh "$s" || die "ingest $s failed"
  echo "::endgroup::"
done

WN_SRC="public.laplace_hash128_blake3(convert_to('substrate/source/WordNetDecomposer/v1','UTF8'))"

# Regression gate: WordNet must emit CONTENT physicalities (kind=1). Zero before the fix.
wn_content=$("${PSQL[@]}" "SELECT count(*) FROM laplace.physicalities WHERE kind = 1 AND source_id = ${WN_SRC};")
log "WordNet CONTENT physicalities = ${wn_content}"
[[ "${wn_content:-0}" -gt 0 ]] || die "WordNet emitted no CONTENT physicalities — content path regressed (string-keyed / attestations-only)"

wn_att=$("${PSQL[@]}" "SELECT count(*) FROM laplace.attestations WHERE source_id = ${WN_SRC};")
log "WordNet attestations = ${wn_att}"
[[ "${wn_att:-0}" -gt 0 ]] || die "WordNet emitted no attestations"

# Informational: entities witnessed by >1 source (cross-source convergence) — grows once a
# 2nd content source (omw/ud/model) lands; not a hard gate in the bounded ladder.
multi=$("${PSQL[@]}" "SELECT count(*) FROM (SELECT entity_id FROM laplace.physicalities GROUP BY entity_id HAVING count(DISTINCT source_id) > 1) t;")
echo "::notice::decomposer ladder OK — WordNet content=${wn_content}, attestations=${wn_att}, multi-source entities=${multi}"
