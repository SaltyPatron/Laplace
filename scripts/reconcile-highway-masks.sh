#!/usr/bin/env bash
set -euo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
BATCH="${LAPLACE_HIGHWAY_RECONCILE_BATCH:-500000}"

# One connection, set-sized commands and top-level CALLs. Never a per-entity
# application loop, and never a full historical scan after population completes.
psql -X -d "$DB" -U laplace_admin -v ON_ERROR_STOP=1 -v batch="$BATCH" <<'SQL'
SELECT (:'batch'::integer >= 1) AS valid_batch \gset
\if :valid_batch
SELECT to_regclass('laplace.highway_mask_pending') IS NOT NULL
   AND to_regclass('laplace.highway_mask_dirty') IS NOT NULL
   AND to_regclass('laplace.highway_mask_population_state') IS NOT NULL
   AND to_regprocedure('laplace.highway_mask_pending_drain(integer)') IS NOT NULL
   AND to_regprocedure('laplace.highway_mask_rebuild(integer)') IS NOT NULL
   AND to_regprocedure('laplace.highway_mask_drain(integer)') IS NOT NULL AS installed \gset
\if :installed
SELECT consensus.highway_ready() AS ready \gset
\if :ready
SELECT NOT EXISTS(SELECT 1 FROM laplace.highway_mask_population_state
                  WHERE singleton AND complete) AS rebuild \gset
\if :rebuild
\echo highway masks: reconciling existing consensus estate
CALL laplace.highway_mask_rebuild(:'batch'::integer);
\else
CALL laplace.highway_mask_pending_drain(:'batch'::integer);
\endif
-- Clears must follow retained OR deposits: an eviction during the outage must
-- not have its stale bit resurrected by replaying an older exact pair.
CALL laplace.highway_mask_drain(:'batch'::integer);
\else
\echo highway masks: registry unavailable; exact work retained, not reported as populated
\endif
SELECT (SELECT complete FROM laplace.highway_mask_population_state WHERE singleton)
           AS historical_population_complete,
       (SELECT count(*) FROM laplace.highway_mask_pending) AS pending_pairs,
       (SELECT count(*) FROM laplace.highway_mask_dirty) AS pending_clears;
\else
\echo highway masks: reconciliation surface not installed yet
\endif
\else
DO $$ BEGIN RAISE EXCEPTION 'Highway reconciliation batch must be >= 1'; END $$;
\endif
SQL
