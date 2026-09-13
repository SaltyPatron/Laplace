BEGIN;

CREATE FUNCTION pg_temp.explain_json(statement text)
RETURNS json
LANGUAGE plpgsql
AS $body$
DECLARE
    plan json;
BEGIN
    EXECUTE 'EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) ' || statement INTO STRICT plan;
    RETURN plan;
END
$body$;

-- The maintained catalog must already equal the authoritative tree before this
-- fixture changes anything. This catches writes from earlier extension tests that
-- bypassed the statement-wide maintenance owner.
WITH direct AS (
    SELECT type_id, count(*)::bigint AS consensus_rows
    FROM laplace.consensus
    GROUP BY type_id
), maintained AS (
    SELECT type_id, consensus_rows
    FROM converse.relation_band_live_counts
)
SELECT NOT EXISTS (
    (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
    UNION ALL
    (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
) AS initial_summary_exact;

WITH fixture AS (
    SELECT decode(repeat('a1',16),'hex')::bytea AS subject_id,
           decode(repeat('b1',16),'hex')::bytea AS object_id,
           laplace.relation_type_id('IS_A') AS is_a,
           laplace.relation_type_id('CAUSES') AS causes
)
INSERT INTO laplace.consensus(
    id, subject_id, type_id, object_id,
    rating, rd, volatility, witness_count, last_observed_at)
SELECT laplace.consensus_id(subject_id, is_a, object_id),
       subject_id, is_a, object_id,
       1500000000000, 30000000000, 60000000, 1, clock_timestamp()
FROM fixture
UNION ALL
SELECT laplace.consensus_id(subject_id, causes, object_id),
       subject_id, causes, object_id,
       1500000000000, 30000000000, 60000000, 1, clock_timestamp()
FROM fixture;

WITH direct AS (
    SELECT type_id, count(*)::bigint AS consensus_rows
    FROM laplace.consensus
    GROUP BY type_id
), maintained AS (
    SELECT type_id, consensus_rows
    FROM converse.relation_band_live_counts
)
SELECT NOT EXISTS (
    (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
    UNION ALL
    (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
) AS insert_summary_exact;

WITH fixture AS (
    SELECT decode(repeat('a1',16),'hex')::bytea AS subject_id,
           decode(repeat('b1',16),'hex')::bytea AS object_id,
           laplace.relation_type_id('IS_A') AS is_a,
           laplace.relation_type_id('CAUSES') AS causes
)
UPDATE laplace.consensus AS c
SET type_id = fixture.causes,
    id = laplace.consensus_id(c.subject_id, fixture.causes, c.object_id)
FROM fixture
WHERE c.id = laplace.consensus_id(fixture.subject_id, fixture.is_a, fixture.object_id)
  AND c.type_id = fixture.is_a
  AND c.subject_id = fixture.subject_id;

WITH direct AS (
    SELECT type_id, count(*)::bigint AS consensus_rows
    FROM laplace.consensus
    GROUP BY type_id
), maintained AS (
    SELECT type_id, consensus_rows
    FROM converse.relation_band_live_counts
)
SELECT NOT EXISTS (
    (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
    UNION ALL
    (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
) AS update_summary_exact;

WITH fixture AS (
    SELECT decode(repeat('a1',16),'hex')::bytea AS subject_id,
           decode(repeat('b1',16),'hex')::bytea AS object_id,
           laplace.relation_type_id('CAUSES') AS causes
)
DELETE FROM laplace.consensus AS c
USING fixture
WHERE c.id = laplace.consensus_id(fixture.subject_id, fixture.causes, fixture.object_id)
  AND c.type_id = fixture.causes
  AND c.subject_id = fixture.subject_id;

WITH direct AS (
    SELECT type_id, count(*)::bigint AS consensus_rows
    FROM laplace.consensus
    GROUP BY type_id
), maintained AS (
    SELECT type_id, consensus_rows
    FROM converse.relation_band_live_counts
)
SELECT NOT EXISTS (
    (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
    UNION ALL
    (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
) AS delete_summary_exact;

SELECT COALESCE(sum(consensus_rows),0) = (SELECT count(*) FROM laplace.consensus)
       AS band_totals_exact
FROM converse.relation_bands();

SELECT pg_temp.explain_json('SELECT * FROM converse.relation_bands()')::text
       NOT LIKE '%"Relation Name": "consensus"%'
       AS relation_bands_does_not_scan_consensus;

TRUNCATE TABLE laplace.consensus;

SELECT NOT EXISTS (SELECT 1 FROM converse.relation_band_live_counts)
   AND COALESCE((SELECT sum(consensus_rows) FROM converse.relation_bands()),0) = 0
   AS truncate_summary_exact;

ROLLBACK;
