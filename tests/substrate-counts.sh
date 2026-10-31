#!/usr/bin/env bash
# Exercise actual partition statistics, including the desktop's reset counters.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
name="laplace_counts_${GITHUB_RUN_ID:-$$}_${RANDOM}"
P(){ psql -X -v ON_ERROR_STOP=1 -Atq -d "$1" "${@:2}"; }
printf 'CREATE DATABASE "%s";\n' "$name" | P postgres
cleanup(){ printf 'DROP DATABASE "%s" WITH (FORCE);\n' "$name" | P postgres; }
trap cleanup EXIT
P "$name" <<'SQL'
CREATE SCHEMA laplace;
CREATE SCHEMA ops;
CREATE TABLE public.entity(id integer) PARTITION BY HASH(id);
CREATE TABLE public.entity_0 PARTITION OF public.entity FOR VALUES WITH (MODULUS 2, REMAINDER 0);
CREATE TABLE public.entity_1 PARTITION OF public.entity FOR VALUES WITH (MODULUS 2, REMAINDER 1);
CREATE TABLE public.physicality(id integer);
CREATE TABLE public.consensus(id integer);
INSERT INTO public.entity SELECT generate_series(1,120);
INSERT INTO public.physicality SELECT generate_series(1,80);
INSERT INTO public.consensus SELECT generate_series(1,40);
SELECT pg_stat_force_next_flush();
ANALYZE public.entity;
ANALYZE public.physicality;
ANALYZE public.consensus;
SQL
cat "$root/extension/laplace_substrate/sql/functions/ops/substrate_counts.sql.in" | P "$name"
check(){
  local expected="$1" actual
  actual=$(P "$name" -c "SELECT string_agg(value::text, ',' ORDER BY metric) FROM ops.substrate_counts()")
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL expected $expected, observed $actual" >&2; exit 1
  fi
}
check '40,120,80'
assert_sql(){
  local actual
  actual=$(P "$name" -c "$1")
  [[ "$actual" == t ]] || { echo "FAIL $1 returned $actual" >&2; exit 1; }
}
# A pre-upgrade source has unknown assertion cardinality, never a measured zero.
assert_sql "SELECT value IS NULL FROM ops.substrate_counts() WHERE metric='attestations(MEASURED)'"
P "$name" -c 'CREATE TABLE public.witness(id integer,attestations bigint CHECK(attestations>=0)); INSERT INTO public.witness VALUES(1,2),(2,NULL)' >/dev/null
assert_sql 'SELECT ops.attestation_count() IS NULL'
P "$name" -c 'UPDATE public.witness SET attestations=3 WHERE id=2' >/dev/null
assert_sql 'SELECT ops.attestation_count()=5'
assert_sql "SELECT value=5 FROM ops.substrate_counts() WHERE metric='attestations(MEASURED)'"
P "$name" -c 'DELETE FROM public.witness' >/dev/null
assert_sql 'SELECT ops.attestation_count()=0'
P "$name" -c 'DROP TABLE public.witness' >/dev/null
P "$name" -c 'SELECT pg_stat_reset()' >/dev/null
check '40,120,80'
# A real measured zero must override the old nonzero planner estimate.
P "$name" -c 'DELETE FROM public.entity; DELETE FROM public.physicality; DELETE FROM public.consensus; SELECT pg_stat_force_next_flush()' >/dev/null
check '0,0,0'
# Between ANALYZE runs, new committed rows must be visible through live stats.
P "$name" -c 'INSERT INTO public.entity SELECT generate_series(1,150); SELECT pg_stat_force_next_flush()' >/dev/null
check '0,150,0'
P "$name" -c 'TRUNCATE public.entity; ANALYZE public.entity' >/dev/null
check '0,0,0'
# The compatibility installation selects its own tables and attestation metric.
P "$name" <<'SQL'
DROP TABLE public.entity,public.physicality,public.consensus;
CREATE TABLE laplace.entities(id integer);
CREATE TABLE laplace.physicalities(id integer);
CREATE TABLE laplace.consensus(id integer);
CREATE TABLE laplace.attestations(id integer);
INSERT INTO laplace.entities SELECT generate_series(1,30);
SELECT pg_stat_force_next_flush();
ANALYZE laplace.entities;
SELECT pg_stat_reset();
SQL
check '0,0,30,0'
echo 'PASS Native and compatibility estimates survive statistics reset, preserve measured empty tables, and include committed inserts'
