#!/usr/bin/env bash
# scripts/e2e-substrate.sh — the end-to-end substrate rebuild, as a first-class
# repo script (no session-local runbooks):
#
#   db-fresh (drop + recreate + migrate + T0 seed)
#   → unicode via IngestRunner (idempotent re-pass; writes the layer-0
#     marker that the bulk T0 seed does not)
#   → iso639
# → each model dir given on the command line, one at a time (the
#     one-at-a-time law). Each completed ingest period folds consensus at its
#     end (watermark window) — consensus exists WITHOUT any batch pass.
#   → audit: verify-fk + table counts + consensus_stats + DB size.
#
# DESTRUCTIVE on the target DB (db-fresh nukes it). Defaults to laplace-dev —
# the dev/testing DB — and pins BOTH consumers' env (Migrations reads
# PGDATABASE; the CLI reads LAPLACE_DB) so no stage can fall back to the CI DB.
#
# Usage:
#   scripts/e2e-substrate.sh [model-dir ...]
#   LAPLACE_E2E_DB=somedb scripts/e2e-substrate.sh ...   # override target DB
#
# A killed run resumes lawfully WITHOUT db-fresh: just re-run the model ingest
# (scripts/ingest-source.sh model <dir>) — the completion-marker guard admits
# continuation, dedup skims landed rows, and the watermark fold covers the
# orphaned window at the clean end. Re-running THIS script starts from zero by
# design.

set -o pipefail
cd "$(cd "$(dirname "$0")/.." && pwd)"
T_START=$SECONDS

DB="${LAPLACE_E2E_DB:-laplace-dev}"
export PGDATABASE="$DB"
export LAPLACE_DB="Host=/var/run/postgresql;Username=laplace_admin;Database=$DB;Search Path=laplace,public"
export LAPLACE_SKIP_MORPH="${LAPLACE_SKIP_MORPH:-1}"
# Intel setvars trips set -e (nonzero when already initialized) AND set -u
# (its unbound-var references are shell-FATAL under -u; `|| true` cannot catch
# them) — so ALL strict flags come AFTER the source.
source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
set -eu
export LD_LIBRARY_PATH="$PWD/build/engine/synthesis:$PWD/build/engine/core:$PWD/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
# T0 perf-cache needs no env: the CLI discovers it (env → /opt/laplace/share →
# build tree). LD_LIBRARY_PATH above is the workspace-relative engine path.

# Model dirs by CONVENTION (never hardcoded snapshot SHAs): explicit args, else
# $LAPLACE_TINYLLAMA_DIR / $LAPLACE_PHI2_DIR, else the newest snapshot with
# weights under each family's HF cache dir.
newest_snapshot() { # newest_snapshot <models--ORG--NAME glob>
    local fam snap
    for fam in /vault/models/$1; do
        [ -d "$fam/snapshots" ] || continue
        for snap in $(ls -t "$fam/snapshots" 2>/dev/null); do
            if ls "$fam/snapshots/$snap"/*.safetensors >/dev/null 2>&1; then
                echo "$fam/snapshots/$snap"; return 0
            fi
        done
    done
    return 1
}
MODELS=("$@")
if [ ${#MODELS[@]} -eq 0 ]; then
    TINY="${LAPLACE_TINYLLAMA_DIR:-$(newest_snapshot 'models--TinyLlama--*' || true)}"
    PHI2="${LAPLACE_PHI2_DIR:-$(newest_snapshot 'models--microsoft--phi-2' || true)}"
    [ -n "$TINY" ] || { echo "no TinyLlama model resolved (set LAPLACE_TINYLLAMA_DIR)"; exit 2; }
    MODELS=("$TINY")
    [ -n "$PHI2" ] && MODELS+=("$PHI2")
fi

phase() { echo ""; echo "############ PHASE $1 — $2 — t+$((SECONDS-T_START))s $(date -u +%H:%M:%S) ############"; }

phase 1 "db-fresh: drop $DB, recreate, migrate, seed T0 unicode"
just db-fresh

phase 1b "unicode via IngestRunner — idempotent re-pass; writes the layer-0 marker"
scripts/ingest-source.sh unicode

phase 2 "seed iso639"
scripts/ingest-source.sh iso639

n=3
for m in "${MODELS[@]}"; do
    phase "$n" "ingest model $(basename "$(dirname "$(dirname "$m")")" 2>/dev/null || basename "$m")"
    scripts/ingest-source.sh model "$m"
    n=$((n+1))
done

phase "$n" "audit — referential integrity + substrate state + consensus"
psql -U laplace_admin -d "$DB" -f scripts/verify-fk.sql
psql -U laplace_admin -d "$DB" -P pager=off -c "
SELECT 'entities' t, count(*) FROM laplace.entities
UNION ALL SELECT 'physicalities', count(*) FROM laplace.physicalities
UNION ALL SELECT 'attestations', count(*) FROM laplace.attestations
UNION ALL SELECT 'consensus', count(*) FROM laplace.consensus;
SELECT * FROM laplace.consensus_stats();
SELECT pg_size_pretty(pg_database_size('$DB')) AS db_size;"

echo ""
echo "############ END-TO-END COMPLETE in $((SECONDS-T_START))s ############"
