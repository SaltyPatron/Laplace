#!/usr/bin/env bash

set -o pipefail
cd "$(cd "$(dirname "$0")/.." && pwd)"
T_START=$SECONDS

DB="${LAPLACE_E2E_DB:-laplace-dev}"
export PGDATABASE="$DB"
export LAPLACE_DB="Host=/var/run/postgresql;Username=laplace_admin;Database=$DB;Search Path=laplace,public"
export LAPLACE_SKIP_MORPH="${LAPLACE_SKIP_MORPH:-1}"
source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
set -eu
export LD_LIBRARY_PATH="$PWD/build/engine/synthesis:$PWD/build/engine/core:$PWD/build/engine/dynamics:${LD_LIBRARY_PATH:-}"

newest_snapshot() {
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
SELECT * FROM laplace.substrate_counts();
SELECT * FROM laplace.consensus_stats();
SELECT pg_size_pretty(pg_database_size('$DB')) AS db_size;"

echo ""
echo "############ END-TO-END COMPLETE in $((SECONDS-T_START))s ############"
