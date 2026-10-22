#!/usr/bin/env bash
# Run the actual health predicate against PostgreSQL's parsed SQL dependency catalog.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
query=$(awk '
  /^WITH RECURSIVE functions\(oid\) AS \(/ { capture=1 }
  capture { if ($0 ~ /"\)$/) { sub(/"\)$/, ""); print; exit }; print }
' "$root/scripts/check-database-health.sh")
[[ -n "$query" ]] || { echo 'Missing production band dependency predicate' >&2; exit 1; }
psql -X -v ON_ERROR_STOP=1 -d postgres <<SQL
BEGIN;
CREATE SCHEMA laplace;
CREATE SCHEMA converse;
CREATE SCHEMA ops;
CREATE TABLE laplace.consensus(type_id bytea);
CREATE FUNCTION laplace.relation_registry() RETURNS TABLE(band integer, type_id bytea)
LANGUAGE sql IMMUTABLE BEGIN ATOMIC VALUES (2, '\x01'::bytea); END;
\i '$root/extension/laplace_substrate/sql/functions/converse/relation_bands.sql.in'
CREATE TEMP VIEW band_health_contract(ok) AS $query
DO \$test\$ BEGIN
  IF NOT (SELECT ok FROM band_health_contract) THEN
    RAISE EXCEPTION 'Current delegated census rejected';
  END IF;
END \$test\$;
SELECT band, name, rank, relation_types, consensus_rows FROM converse.relation_bands() WHERE false;
CREATE OR REPLACE FUNCTION converse.relation_bands()
RETURNS TABLE(band integer, name text, rank double precision, relation_types bigint, consensus_rows bigint)
LANGUAGE sql STABLE BEGIN ATOMIC
  SELECT r.band, 'direct'::text, 1.0::double precision, 1::bigint, 0::bigint
  FROM laplace.relation_registry() r;
END;
DO \$test\$ BEGIN
  IF NOT (SELECT ok FROM band_health_contract) THEN RAISE EXCEPTION 'Direct registry dependency rejected'; END IF;
END \$test\$;
CREATE OR REPLACE FUNCTION converse.relation_bands()
RETURNS TABLE(band integer, name text, rank double precision, relation_types bigint, consensus_rows bigint)
LANGUAGE sql STABLE BEGIN ATOMIC
  SELECT 0, 'stale'::text, 1.0::double precision, 1::bigint, count(*) FROM laplace.consensus;
END;
DO \$test\$ BEGIN
  IF (SELECT ok FROM band_health_contract) THEN RAISE EXCEPTION 'Stale direct consensus scan accepted'; END IF;
END \$test\$;
CREATE OR REPLACE FUNCTION converse.relation_bands()
RETURNS TABLE(band integer, name text, rank double precision, relation_types bigint, consensus_rows bigint)
LANGUAGE sql STABLE BEGIN ATOMIC VALUES (0, 'disconnected'::text, 1.0::double precision, 0::bigint, 0::bigint); END;
DO \$test\$ BEGIN
  IF (SELECT ok FROM band_health_contract) THEN RAISE EXCEPTION 'Missing registry dependency accepted'; END IF;
END \$test\$;
ROLLBACK;
SQL
echo 'PASS actual delegated census and direct registry; rejected stale and disconnected functions'
