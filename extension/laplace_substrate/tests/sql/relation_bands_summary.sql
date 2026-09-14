-- relation_bands() is an interactive catalog surface. Its exact live counts must be
-- maintained transactionally by statement-wide consensus deltas; the read itself
-- must never aggregate the partitioned consensus tree.
\set ECHO none
BEGIN;

DO $relation_bands_summary$
DECLARE
    v_subject_id bytea := decode(repeat('a1',16),'hex');
    v_object_is_a bytea := decode(repeat('b1',16),'hex');
    v_object_causes bytea := decode(repeat('b2',16),'hex');
    v_is_a bytea := laplace.relation_type_id('IS_A');
    v_causes bytea := laplace.relation_type_id('CAUSES');
    v_plan json;
BEGIN
    IF EXISTS (
        WITH direct AS (
            SELECT type_id, count(*)::bigint AS consensus_rows
            FROM laplace.consensus GROUP BY type_id
        ), maintained AS (
            SELECT type_id, consensus_rows
            FROM converse.relation_band_live_counts
        )
        (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
        UNION ALL
        (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
    ) THEN
        RAISE EXCEPTION 'relation-band summary disagrees before mutation';
    END IF;

    INSERT INTO laplace.consensus(
        id, subject_id, type_id, object_id,
        rating, rd, volatility, witness_count, last_observed_at)
    VALUES
      (laplace.consensus_id(v_subject_id, v_is_a, v_object_is_a),
       v_subject_id, v_is_a, v_object_is_a,
       1500000000000, 30000000000, 60000000, 1, clock_timestamp()),
      (laplace.consensus_id(v_subject_id, v_causes, v_object_causes),
       v_subject_id, v_causes, v_object_causes,
       1500000000000, 30000000000, 60000000, 1, clock_timestamp());

    IF EXISTS (
        WITH direct AS (
            SELECT type_id, count(*)::bigint AS consensus_rows
            FROM laplace.consensus GROUP BY type_id
        ), maintained AS (
            SELECT type_id, consensus_rows
            FROM converse.relation_band_live_counts
        )
        (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
        UNION ALL
        (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
    ) THEN
        RAISE EXCEPTION 'relation-band summary disagrees after insert';
    END IF;

    UPDATE laplace.consensus AS c
    SET type_id = v_causes,
        id = laplace.consensus_id(c.subject_id, v_causes, c.object_id)
    WHERE c.id = laplace.consensus_id(v_subject_id, v_is_a, v_object_is_a)
      AND c.type_id = v_is_a
      AND c.subject_id = v_subject_id;

    IF EXISTS (
        WITH direct AS (
            SELECT type_id, count(*)::bigint AS consensus_rows
            FROM laplace.consensus GROUP BY type_id
        ), maintained AS (
            SELECT type_id, consensus_rows
            FROM converse.relation_band_live_counts
        )
        (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
        UNION ALL
        (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
    ) THEN
        RAISE EXCEPTION 'relation-band summary disagrees after partition-key update';
    END IF;

    DELETE FROM laplace.consensus AS c
    WHERE c.id = laplace.consensus_id(v_subject_id, v_causes, v_object_causes)
      AND c.type_id = v_causes
      AND c.subject_id = v_subject_id;

    IF EXISTS (
        WITH direct AS (
            SELECT type_id, count(*)::bigint AS consensus_rows
            FROM laplace.consensus GROUP BY type_id
        ), maintained AS (
            SELECT type_id, consensus_rows
            FROM converse.relation_band_live_counts
        )
        (SELECT * FROM direct EXCEPT ALL SELECT * FROM maintained)
        UNION ALL
        (SELECT * FROM maintained EXCEPT ALL SELECT * FROM direct)
    ) THEN
        RAISE EXCEPTION 'relation-band summary disagrees after delete';
    END IF;

    IF (SELECT COALESCE(sum(consensus_rows),0) FROM converse.relation_bands())
       <> (SELECT count(*) FROM laplace.consensus) THEN
        RAISE EXCEPTION 'relation-band totals disagree with consensus';
    END IF;

    EXECUTE 'EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) '
            'SELECT * FROM converse.relation_bands()'
    INTO STRICT v_plan;
    IF v_plan::text LIKE '%"Relation Name": "consensus"%' THEN
        RAISE EXCEPTION 'relation_bands still scans consensus: %', v_plan;
    END IF;

    TRUNCATE TABLE laplace.consensus;
    IF EXISTS (SELECT 1 FROM converse.relation_band_live_counts) OR
       COALESCE((SELECT sum(consensus_rows) FROM converse.relation_bands()),0) <> 0 THEN
        RAISE EXCEPTION 'relation-band summary disagrees after truncate';
    END IF;

    RAISE NOTICE 'relation bands summary: exact set-maintained counts and no consensus read scan pass';
END
$relation_bands_summary$;

ROLLBACK;
