#!/usr/bin/env bash
# Seed-agnostic database/substrate health check.
#
# This answers one question only: did database creation/migration/extension sync
# leave a structurally usable Laplace substrate? It deliberately does NOT require
# foundation/content data and does NOT call converse/generation/model/chess AI
# surfaces. Seed completeness belongs to check-substrate-floor.sh; product behavior
# belongs to Tier=live/eval/smoke.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/database.sh"
require_source_match=1
case "${LAPLACE_DB_HEALTH_SCOPE:-source}" in
  source|"") ;;
  installed) require_source_match=0 ;;
  *)
    echo "invalid LAPLACE_DB_HEALTH_SCOPE: ${LAPLACE_DB_HEALTH_SCOPE}" >&2
    exit 2
    ;;
esac
if [[ "${1:-}" == "--installed-runtime" ]]; then
  require_source_match=0
  shift
fi
[[ "$#" -le 1 ]] || {
  echo "usage: $0 [--installed-runtime] [database]" >&2
  exit 2
}
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
  "SELECT extversion FROM pg_extension WHERE extname = 'laplace'" 2>/dev/null || true)
[[ -n "$ext" ]] || fail "laplace extension is not installed in '$DB'"

source_ext="not-required"
artifact_scope="installed"
if [[ "$require_source_match" == 1 ]]; then
  source_ext=$("${PSQL[@]}" -d "$DB" -tAc "SELECT default_version FROM pg_available_extensions WHERE name = 'laplace';" 2>/dev/null || true)
  [[ -n "$source_ext" ]] || fail "could not determine the available laplace extension version"
  [[ "$ext" == "$source_ext" ]] || \
    fail "live laplace extension version is stale: database=$ext available=$source_ext"
  artifact_scope="source"
else
  installed_match=$("${PSQL[@]}" -d "$DB" -tAc "
SELECT count(*)
FROM pg_extension e
JOIN pg_available_extension_versions a
  ON a.name = e.extname AND a.version = e.extversion
WHERE e.extname = 'laplace';")
  [[ "${installed_match:-0}" != "0" ]] || \
    fail "live laplace version $ext is not present in installed extension artifacts"
fi

missing=$("${PSQL[@]}" -d "$DB" -tAc "
WITH required(name) AS (VALUES
  ('public.entity'),
  ('public.physicality'),
  ('public.attestation'),
  ('public.consensus')
)
SELECT string_agg(name, ', ' ORDER BY name)
FROM required
WHERE to_regclass(name) IS NULL;")
[[ -z "$missing" ]] || fail "required canonical substrate relations missing: $missing"

if ! fp=$("${PSQL[@]}" -d "$DB" -tAc "SELECT public.laplace_fingerprint()" 2>/dev/null) || [[ -z "$fp" ]]; then
  fail "public.laplace_fingerprint() failed or returned empty in '$DB'"
fi
if ! hw=$("${PSQL[@]}" -d "$DB" -tAc "SELECT public.laplace_highway_fingerprint()" 2>/dev/null) || [[ -z "$hw" ]]; then
  fail "public.laplace_highway_fingerprint() failed or returned empty in '$DB'"
fi
sub_counts=$("${PSQL[@]}" -d "$DB" -tAc "SELECT count(*) FROM ops.substrate_counts();" 2>/dev/null || true)
[[ "${sub_counts:-0}" == "4" ]] || fail "ops.substrate_counts() expected 4 rows, got '${sub_counts:-}'"

invalid=$("${PSQL[@]}" -d "$DB" -tAc "
SELECT count(*)
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ('public','ops','stage')
  AND NOT i.indisvalid;")
if [[ "${invalid:-0}" != "0" ]]; then
  "${PSQL[@]}" -d "$DB" -P pager=off -c "
  SELECT n.nspname AS schema_name, c.relname AS index_name,
         i.indisready, i.indisvalid, i.indislive
  FROM pg_index i
  JOIN pg_class c ON c.oid = i.indexrelid
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname IN ('public','ops','stage')
    AND NOT i.indisvalid
  ORDER BY 1, 2;" || true
  fail "$invalid invalid substrate index(es)"
fi

unvalidated=$("${PSQL[@]}" -d "$DB" -tAc "
SELECT count(*)
FROM pg_constraint c
JOIN pg_namespace n ON n.oid = c.connamespace
WHERE n.nspname IN ('public','ops','stage')
  AND NOT c.convalidated;")
if [[ "${unvalidated:-0}" != "0" ]]; then
  "${PSQL[@]}" -d "$DB" -P pager=off -c "
  SELECT n.nspname AS schema_name, c.conname, c.contype,
         c.conrelid::regclass AS relation
  FROM pg_constraint c
  JOIN pg_namespace n ON n.oid = c.connamespace
  WHERE n.nspname IN ('public','ops','stage')
    AND NOT c.convalidated
  ORDER BY 1, 2;" || true
  fail "$unvalidated unvalidated substrate constraint(s)"
fi

echo "DB_HEALTH_OK database=$DB extension=$ext source_extension=$source_ext artifact_scope=$artifact_scope required_relations=4 invalid_indexes=0 unvalidated_constraints=0 seed_state=not_required"
