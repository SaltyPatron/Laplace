#!/usr/bin/env bash
# Exercise the migration with an isolated database and unprivileged runtime role.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
name="laplace_grants_${GITHUB_RUN_ID:-$$}_${RANDOM}"
role="${name}_runtime"
owner="${name}_owner"
P(){ psql -X -v ON_ERROR_STOP=1 -Atq -d "$1" "${@:2}"; }
printf 'CREATE DATABASE "%s";\n' "$name" | P postgres
cleanup(){
  printf 'DROP DATABASE "%s" WITH (FORCE);\nDROP OWNED BY "%s";\nDROP ROLE IF EXISTS "%s";\nDROP OWNED BY "%s";\nDROP ROLE IF EXISTS "%s";\n' "$name" "$owner" "$owner" "$role" "$role" | P postgres
}
trap cleanup EXIT
printf 'CREATE ROLE "%s" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;\n' "$role" | P postgres
printf 'CREATE ROLE "%s" NOLOGIN;\n' "$owner" | P postgres
P "$name" <<'SQL'
CREATE SCHEMA laplace;
CREATE SCHEMA consensus;
CREATE SCHEMA ops;
CREATE SCHEMA app;
CREATE TABLE laplace.entities(id serial PRIMARY KEY, value text);
CREATE TABLE consensus.claims(id integer);
CREATE TABLE ops.events(id integer);
CREATE TABLE app.accounts(id integer);
CREATE TABLE public.reference(id integer);
INSERT INTO public.reference VALUES (42);
-- The substrate shape: hash-partitioned tables, as the extension creates them.
CREATE TABLE laplace.attestations(id integer) PARTITION BY HASH (id);
CREATE TABLE laplace.attestations_h0 PARTITION OF laplace.attestations FOR VALUES WITH (MODULUS 2, REMAINDER 0);
CREATE TABLE laplace.attestations_h1 PARTITION OF laplace.attestations FOR VALUES WITH (MODULUS 2, REMAINDER 1);
INSERT INTO laplace.attestations SELECT generate_series(1, 200);
SQL
# A substrate table another role owns (db-reset creates the extension's tables as postgres).
P "$name" -v owner="$owner" <<'SQL'
GRANT USAGE, CREATE ON SCHEMA consensus TO :"owner";
SET ROLE :"owner";
CREATE TABLE consensus.cells(id integer) PARTITION BY HASH (id);
CREATE TABLE consensus.cells_h0 PARTITION OF consensus.cells FOR VALUES WITH (MODULUS 2, REMAINDER 0);
RESET ROLE;
SQL
for iteration in 1 2; do
  for migration in 20261015000000_substrate_runtime_grants 20261017000000_substrate_runtime_maintain; do
    sed "s/'laplace_runtime'/'$role'/" "$root/db/migrations/$migration.sql" | P "$name"
  done
done
P "$name" -v role="$role" <<'SQL'
CREATE TABLE laplace.future_entity(id serial PRIMARY KEY);
SET ROLE :"role";
INSERT INTO laplace.entities(value) VALUES ('witnessed fixture');
UPDATE laplace.entities SET value='updated fixture';
SELECT count(*) FROM laplace.entities;
INSERT INTO consensus.claims VALUES (1);
INSERT INTO ops.events VALUES (1);
INSERT INTO laplace.future_entity DEFAULT VALUES;
SELECT id FROM public.reference;
DELETE FROM laplace.entities;
RESET ROLE;
SELECT NOT has_schema_privilege(:'role','laplace','CREATE')
   AND NOT has_schema_privilege(:'role','app','USAGE')
   AND NOT has_table_privilege(:'role','app.accounts','SELECT')
   AND NOT has_table_privilege(:'role','public.reference','INSERT')
   AND NOT pg_has_role(:'role',current_user,'MEMBER') AS restricted \gset
\if :restricted
\else
\quit 1
\endif
SQL
# The runtime refreshes the statistics of what it writes: ANALYZE skips, with only a WARNING,
# any table or partition it may not MAINTAIN (Laplace#1879), so a warning here is a failure.
P "$name" -v owner="$owner" <<'SQL'
CREATE TABLE laplace.physicalities(id integer) PARTITION BY HASH (id);
CREATE TABLE laplace.physicalities_h0 PARTITION OF laplace.physicalities FOR VALUES WITH (MODULUS 1, REMAINDER 0);
INSERT INTO laplace.physicalities SELECT generate_series(1, 30);
SET ROLE :"owner";
CREATE TABLE consensus.cells_h1 PARTITION OF consensus.cells FOR VALUES WITH (MODULUS 2, REMAINDER 1);
RESET ROLE;
INSERT INTO consensus.cells SELECT generate_series(1, 50);
SQL
analyze_log=$(P "$name" -v role="$role" 2>&1 <<'SQL'
SET ROLE :"role";
ANALYZE laplace.attestations;
ANALYZE consensus.cells;
ANALYZE laplace.physicalities;
RESET ROLE;
SQL
)
if grep -qi 'warning\|skipping\|permission denied' <<<"$analyze_log"; then
  echo "FAIL runtime ANALYZE skipped a relation: $analyze_log" >&2; exit 1
fi
P "$name" -v role="$role" <<'SQL'
SELECT bool_and(has_table_privilege(:'role', p.relid, 'MAINTAIN')) AND count(*) = 8 AS every_leaf_maintainable
FROM unnest(ARRAY['laplace.attestations'::regclass, 'consensus.cells'::regclass,
                  'laplace.physicalities'::regclass]) t(rel)
CROSS JOIN LATERAL pg_partition_tree(t.rel) p \gset
\if :every_leaf_maintainable
\else
\echo 'FAIL a substrate table or partition is not maintainable by the runtime'
\quit 1
\endif
SELECT (SELECT sum(reltuples) FROM pg_class WHERE relname IN ('attestations_h0','attestations_h1')) = 200
   AND (SELECT sum(reltuples) FROM pg_class WHERE relname IN ('cells_h0','cells_h1')) = 50
   AND (SELECT reltuples FROM pg_class WHERE oid = 'laplace.attestations'::regclass) = 200
   AND (SELECT reltuples FROM pg_class WHERE relname = 'physicalities_h0') = 30
   AS analyzed \gset
\if :analyzed
\else
\echo 'FAIL the runtime ANALYZE left reltuples unset'
\quit 1
\endif
SELECT NOT has_table_privilege(:'role', 'public.reference', 'MAINTAIN') AS public_not_maintained \gset
\if :public_not_maintained
\else
\quit 1
\endif
SQL
echo 'PASS runtime MAINTAINs every substrate table and partition, existing and future, its own or another owner'"'"'s, and its ANALYZE refreshes reltuples with no skip'
echo 'PASS runtime can use existing and future substrate objects, without application, DDL, public-write or owner access'
