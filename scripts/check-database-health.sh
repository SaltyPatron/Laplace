#!/usr/bin/env bash
# Seed-agnostic database/substrate health check.
#
# This answers one question only: did database creation/migration/extension sync
# leave a structurally usable Laplace substrate? It deliberately does NOT require
# foundation/knowledge data and does NOT call converse/generation/model/chess AI
# surfaces. Seed completeness belongs to check-substrate-floor.sh; product behavior
# belongs to Tier=live/eval/smoke.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DB="${1:-${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}}"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1 -X)

fail() {
  echo "::error::DB_HEALTH: $*" >&2
  exit 1
}

# `psql -c` sends a server-parsable command string; it does not perform psql
# variable interpolation inside that command. The former pg_database probe passed
# `:'db'` through -c, suppressed the resulting syntax error, and therefore reported
# every healthy database as absent (#1365). Connect to the target database directly:
# that proves the stronger condition this gate actually needs without SQL quoting.
if ! probe=$("${PSQL[@]}" -d "$DB" -tAc "SELECT 1" 2>/dev/null); then
  fail "database '$DB' is not connectable"
fi
[[ "$probe" == "1" ]] || fail "database '$DB' failed connection probe"

ext=$("${PSQL[@]}" -d "$DB" -tAc \
  "SELECT extversion FROM pg_extension WHERE extname = 'laplace_substrate'" 2>/dev/null || true)
[[ -n "$ext" ]] || fail "laplace_substrate extension is not installed in '$DB'"

# Source, installed extension artifacts, and the database catalog must name the same
# content-derived extension version. A green regression against a stale installed SQL
# artifact or a successful source build cannot establish that the live database was
# upgraded. This gate runs after sync-extension and therefore treats any mismatch as a
# failed deployment, not as an informational warning.
if ! "$SCRIPT_DIR/check-installed-extension-current.py" >/dev/null; then
  fail "installed laplace_substrate artifacts do not match the current source"
fi
source_ext=$("$SCRIPT_DIR/check-installed-extension-current.py" --print-source-version)
[[ -n "$source_ext" ]] || fail "could not determine the source laplace_substrate version"
[[ "$ext" == "$source_ext" ]] || \
  fail "live laplace_substrate version is stale: database=$ext source=$source_ext"

missing=$("${PSQL[@]}" -d "$DB" -tAc "
WITH required(name) AS (VALUES
  ('laplace.entities'),
  ('laplace.physicalities'),
  ('laplace.attestations'),
  ('laplace.consensus'),
  ('laplace.ingest_run_journal'),
  ('laplace.ingest_flush_journal')
)
SELECT string_agg(name, ', ' ORDER BY name)
FROM required
WHERE to_regclass(name) IS NULL;")
[[ -z "$missing" ]] || fail "required substrate relations missing: $missing"

# relation_bands() used to aggregate the complete consensus tree at read time. The
# current implementation maintains exact counts transactionally in a compact catalog.
# Verify the live function body and supporting relation after extension synchronization
# so an old full-scan definition cannot continue serving traffic unnoticed.
relation_band_counts=$("${PSQL[@]}" -d "$DB" -tAc \
  "SELECT to_regclass('converse.relation_band_live_counts') IS NOT NULL;")
[[ "$relation_band_counts" == "t" ]] || \
  fail "relation-band live-count catalog is missing; extension upgrade is incomplete"
relation_bands_def=$("${PSQL[@]}" -d "$DB" -tAc \
  "SELECT pg_get_functiondef('converse.relation_bands()'::regprocedure);")
[[ "$relation_bands_def" == *"relation_band_live_counts"* ]] || \
  fail "converse.relation_bands() is stale and does not use maintained live counts"
[[ "$relation_bands_def" != *"FROM laplace.consensus"* ]] || \
  fail "converse.relation_bands() still performs a full consensus-tree read"

# Bind the native attestation COPY surface without reading or writing evidence.
# Table existence and an extension version alone do not prove an upgrade applied
# the additive columns required by the current writer.
if ! "${PSQL[@]}" -d "$DB" -tAc "
SELECT id, subject_id, type_id, object_id, source_id, context_id, outcome,
       last_observed_at, observation_count, sum_score_fp1e9,
       opponent_rd_fp1e9, opponent_rating_fp1e9, fold_replayable, highway_mask
FROM laplace.attestations WHERE false;" >/dev/null; then
  fail "attestation writer columns are missing; extension schema upgrade is incomplete"
fi

invalid=$("${PSQL[@]}" -d "$DB" -tAc "
SELECT count(*)
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ('laplace','consensus','converse','lexical','taxonomy','generation','structural','realize','ops')
  AND NOT i.indisvalid;")
if [[ "${invalid:-0}" != "0" ]]; then
  "${PSQL[@]}" -d "$DB" -P pager=off -c "
  SELECT n.nspname AS schema_name, c.relname AS index_name,
         i.indisready, i.indisvalid, i.indislive
  FROM pg_index i
  JOIN pg_class c ON c.oid = i.indexrelid
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname IN ('laplace','consensus','converse','lexical','taxonomy','generation','structural','realize','ops')
    AND NOT i.indisvalid
  ORDER BY 1, 2;" || true
  fail "$invalid invalid substrate index(es)"
fi

unvalidated=$("${PSQL[@]}" -d "$DB" -tAc "
SELECT count(*)
FROM pg_constraint c
JOIN pg_namespace n ON n.oid = c.connamespace
WHERE n.nspname IN ('laplace','consensus','converse','lexical','taxonomy','generation','structural','realize','ops')
  AND NOT c.convalidated;")
if [[ "${unvalidated:-0}" != "0" ]]; then
  "${PSQL[@]}" -d "$DB" -P pager=off -c "
  SELECT n.nspname AS schema_name, c.conname, c.contype,
         c.conrelid::regclass AS relation
  FROM pg_constraint c
  JOIN pg_namespace n ON n.oid = c.connamespace
  WHERE n.nspname IN ('laplace','consensus','converse','lexical','taxonomy','generation','structural','realize','ops')
    AND NOT c.convalidated
  ORDER BY 1, 2;" || true
  fail "$unvalidated unvalidated substrate constraint(s)"
fi

# A freshly-created DB should normally have zero rows; a standing DB may have
# completed history. Only nonterminal ownership is unhealthy for lifecycle work.
running=$("${PSQL[@]}" -d "$DB" -tAc \
  "SELECT count(*) FROM laplace.ingest_run_journal WHERE status = 'running'")
if [[ "${running:-0}" != "0" ]]; then
  "${PSQL[@]}" -d "$DB" -P pager=off -c "
  SELECT source_name, status, input_units_done, input_units_total,
         now() - started_at AS elapsed, error
  FROM laplace.ingest_run_journal
  WHERE status = 'running'
  ORDER BY started_at;" || true
  fail "$running ingest journal row(s) still running"
fi

# Use the installed health operation as a second, extension-owned index verdict.
# This is structural inspection only; it does not depend on seeded content.
op_invalid=$("${PSQL[@]}" -d "$DB" -tAc "SELECT count(*) FROM ops.index_health();")
if [[ "${op_invalid:-0}" != "0" ]]; then
  "${PSQL[@]}" -d "$DB" -P pager=off -c "SELECT * FROM ops.index_health();" || true
  fail "ops.index_health reports $op_invalid invalid index(es)"
fi

echo "DB_HEALTH_OK database=$DB extension=$ext source_extension=$source_ext relation_bands=maintained_counts required_relations=6 invalid_indexes=0 unvalidated_constraints=0 running_ingests=0 seed_state=not_required"
