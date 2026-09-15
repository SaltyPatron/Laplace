#!/usr/bin/env bash
# Real missing-registry and crash/restart exercise, only inside pr-db-proof's
# private throwaway postmaster. No production GUCs, services or data are touched.
set -euo pipefail
PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
pgdata="${1:?private pgdata required}"
highway="${2:?branch Highway blob required}"
DB="${3:?isolated database name required}"
private_root="$(dirname "$pgdata")"
[[ "$(basename "$private_root")" == laplace-pr-db-proof.* &&
   "$pgdata" == "$private_root/pgdata" &&
   "${PGHOST:-}" == "$private_root/socket" &&
   "${PGPORT:-}" == 55432 && "$DB" == laplace_pr_*_highway ]] || {
  echo "Highway recovery exercise requires pr-db-proof's isolated postmaster" >&2
  exit 2
}
actual_data="$("$PG_PREFIX/bin/psql" -XAt -d postgres -v ON_ERROR_STOP=1 -c 'SHOW data_directory')"
[[ "$(realpath "$actual_data")" == "$(realpath "$pgdata")" ]] || exit 2
[[ -f "$highway" ]] || exit 2
"$PG_PREFIX/bin/createdb" "$DB"
"$PG_PREFIX/bin/psql" -X -d "$DB" -v ON_ERROR_STOP=1 <<'SQL'
CREATE EXTENSION postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;
SQL

# Restart with an actually unavailable registry, not a fake readiness function.
# A new backend must retain valid mask work even when loading the configured
# file raises CONFIG_FILE_ERROR. All input contracts remain native and exact.
printf "\nlaplace_substrate.highway_perfcache_path = '%s/absent-highway.bin'\n" "$private_root" >> "$pgdata/postgresql.conf"
"$PG_PREFIX/bin/pg_ctl" -D "$pgdata" -l "$private_root/postgresql.log" -m fast -w restart >/dev/null
"$PG_PREFIX/bin/psql" -X -d "$DB" -v ON_ERROR_STOP=1 <<'SQL'
DO $$
DECLARE
    a bytea := decode('d673b68115514712b366347069127b01','hex');
    b bytea := decode('d673b68115514712b366347069127b02','hex');
    isa bytea := laplace.relation_type_id('IS_A');
    part bytea := laplace.relation_type_id('HAS_PART');
    n bigint;
BEGIN
    IF consensus.highway_ready() THEN RAISE EXCEPTION 'missing registry reported ready'; END IF;
    n := consensus.highway_mask_deposit(ARRAY[a,b,a,NULL],ARRAY[isa,part,isa,part]);
    IF n<>0 THEN RAISE EXCEPTION 'queued work was reported as updated masks'; END IF;
    IF (SELECT count(*) FROM laplace.highway_mask_pending)<>2 OR
       NOT EXISTS(SELECT 1 FROM laplace.highway_mask_pending WHERE entity_id=a AND type_id=isa) OR
       NOT EXISTS(SELECT 1 FROM laplace.highway_mask_pending WHERE entity_id=b AND type_id=part) THEN
        RAISE EXCEPTION 'unavailable-registry deposit lost exact zipped pairs';
    END IF;
    BEGIN
        PERFORM consensus.highway_mask_deposit(ARRAY[a,b],ARRAY[isa]);
        RAISE EXCEPTION 'unavailable registry allowed a truncated pair batch';
    EXCEPTION WHEN array_subscript_error THEN NULL;
    END;
    BEGIN
        PERFORM consensus.highway_mask_deposit(ARRAY[decode('00','hex')],ARRAY[isa]);
        RAISE EXCEPTION 'unavailable registry accepted a malformed identity';
    EXCEPTION WHEN string_data_length_mismatch THEN NULL;
    END;
    PERFORM consensus.highway_mask_refresh(ARRAY[a]);
END $$;
CALL laplace.highway_mask_pending_drain(1);
CALL laplace.highway_mask_drain(1);
DO $$ BEGIN
    IF (SELECT count(*) FROM laplace.highway_mask_pending)<>2 OR
       (SELECT count(*) FROM laplace.highway_mask_dirty)<>1 THEN
        RAISE EXCEPTION 'unavailable-registry drain discarded retained work';
    END IF;
END $$;
SQL

# An immediate stop forces crash recovery. Reconfigure the real registry and
# prove both WAL-logged worksets survive, then run the normal native replay.
"$PG_PREFIX/bin/pg_ctl" -D "$pgdata" -m immediate -w stop >/dev/null
printf "\nlaplace_substrate.highway_perfcache_path = '%s'\n" "$highway" >> "$pgdata/postgresql.conf"
"$PG_PREFIX/bin/pg_ctl" -D "$pgdata" -l "$private_root/postgresql.log" -w start >/dev/null
"$PG_PREFIX/bin/psql" -X -d "$DB" -v ON_ERROR_STOP=1 <<'SQL'
DO $$ BEGIN
    IF NOT consensus.highway_ready() THEN RAISE EXCEPTION 'registry did not recover'; END IF;
    IF (SELECT count(*) FROM laplace.highway_mask_pending)<>2 OR
       (SELECT count(*) FROM laplace.highway_mask_dirty)<>1 THEN
        RAISE EXCEPTION 'crash recovery lost pending mask work';
    END IF;
END $$;
INSERT INTO laplace.entities(id,tier,type_id)
SELECT DISTINCT entity_id,2,decode('d673b68115514712b366347069127bff','hex')
FROM laplace.highway_mask_pending;
CALL laplace.highway_mask_pending_drain(1);
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM laplace.highway_mask_pending) THEN
        RAISE EXCEPTION 'restored registry did not replay pending pairs';
    END IF;
    IF (SELECT count(*) FROM laplace.entities e WHERE
        (e.id=decode('d673b68115514712b366347069127b01','hex') AND
         consensus.highway_mask_bits(e.highway_mask) @>
           ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('IS_A'))]) OR
        (e.id=decode('d673b68115514712b366347069127b02','hex') AND
         consensus.highway_mask_bits(e.highway_mask) @>
           ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('HAS_PART'))]))<>2 THEN
        RAISE EXCEPTION 'native replay did not populate exact entity masks';
    END IF;
END $$;
CALL laplace.highway_mask_drain(1);
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM laplace.highway_mask_dirty) OR
       EXISTS(SELECT 1 FROM laplace.entities WHERE
              id=decode('d673b68115514712b366347069127b01','hex') AND highway_mask IS NOT NULL) THEN
        RAISE EXCEPTION 'retained clear-work did not follow the older deposit';
    END IF;
END $$;
SQL
"$PG_PREFIX/bin/dropdb" "$DB"
echo 'HIGHWAY_REGISTRY_RECOVERY_OK unavailable=retained crash=retained replay=native clears=applied'
