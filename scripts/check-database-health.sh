#!/usr/bin/env bash
# Seed-agnostic database/substrate health check.
#
# This answers one question only: did database creation/migration/extension sync
# leave a structurally usable Laplace substrate? It deliberately does NOT require
# foundation/knowledge data and does NOT call converse/generation/model/chess AI
# surfaces. Seed completeness belongs to check-substrate-floor.sh; product behavior
# belongs to Tier=live/eval/smoke.
set -euo pipefail

DB="${1:-${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}}"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1 -X)

fail() {
  echo "::error::DB_HEALTH: $*" >&2
  exit 1
}

exists=$("${PSQL[@]}" -d postgres --set=db="$DB" -tAc \
  "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db')" 2>/dev/null || true)
[[ "$exists" == "t" ]] || fail "database '$DB' does not exist"

ext=$("${PSQL[@]}" -d "$DB" -tAc \
  "SELECT extversion FROM pg_extension WHERE extname = 'laplace_substrate'" 2>/dev/null || true)
[[ -n "$ext" ]] || fail "laplace_substrate extension is not installed in '$DB'"

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

echo "DB_HEALTH_OK database=$DB extension=$ext required_relations=6 invalid_indexes=0 unvalidated_constraints=0 running_ingests=0 seed_state=not_required"
