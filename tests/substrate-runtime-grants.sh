#!/usr/bin/env bash
# Exercise the migration with an isolated database and unprivileged runtime role.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
name="laplace_grants_${GITHUB_RUN_ID:-$$}_${RANDOM}"
role="${name}_runtime"
P(){ psql -X -v ON_ERROR_STOP=1 -Atq -d "$1" "${@:2}"; }
printf 'CREATE DATABASE "%s";\n' "$name" | P postgres
cleanup(){
  printf 'DROP DATABASE "%s" WITH (FORCE);\nDROP ROLE IF EXISTS "%s";\n' "$name" "$role" | P postgres
}
trap cleanup EXIT
printf 'CREATE ROLE "%s" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;\n' "$role" | P postgres
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
SQL
for iteration in 1 2; do
  sed "s/'laplace_runtime'/'$role'/" "$root/db/migrations/20261015000000_substrate_runtime_grants.sql" | P "$name"
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
echo 'PASS runtime can use existing and future substrate objects, without application, DDL, public-write or owner access'
