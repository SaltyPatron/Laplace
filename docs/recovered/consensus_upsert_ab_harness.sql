\set ON_ERROR_STOP on
SET search_path = laplace, public;

-- ==== v1: exact copy of origin/main consensus_upsert (per-type array re-unnest) ====
CREATE OR REPLACE FUNCTION laplace.consensus_upsert_v1(
    p_subjects bytea[], p_types bytea[], p_objects bytea[],
    p_phis bigint[], p_games bigint[], p_sums bigint[], p_ts timestamptz[])
RETURNS bigint LANGUAGE plpgsql VOLATILE
SET search_path = laplace, public AS $$
DECLARE
    affected bigint := 0;
    v_rc     bigint;
    v_type   bytea;
BEGIN
    IF EXISTS (
        SELECT 1 FROM unnest(p_subjects, p_types, p_objects) AS u(s, t, o)
        GROUP BY u.s, u.t, u.o HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'duplicate cell' USING ERRCODE = 'cardinality_violation';
    END IF;

    FOR v_type IN SELECT DISTINCT t FROM unnest(p_types) AS t LOOP
        UPDATE consensus c SET
            (rating, rd, volatility) = (
                SELECT r.rating, r.rd, r.volatility
                FROM laplace_glicko2_accumulate_games(
                        c.rating, c.rd, c.volatility, glicko2_neutral_mu(),
                        b.phi, b.games, b.sum, glicko2_tau()) AS r),
            witness_count    = c.witness_count + b.games,
            last_observed_at = GREATEST(c.last_observed_at, b.ts)
        FROM (
            SELECT consensus_id(u.s, v_type, u.o) AS id,
                   u.s, u.o, u.phi, u.games, u.sum, u.ts
            FROM unnest(p_subjects, p_types, p_objects,
                        p_phis, p_games, p_sums, p_ts)
                 AS u(s, t, o, phi, games, sum, ts)
            WHERE u.t = v_type
        ) b
        WHERE c.type_id = v_type
          AND c.subject_id = b.s
          AND c.id = b.id;
        GET DIAGNOSTICS v_rc = ROW_COUNT;
        affected := affected + v_rc;

        INSERT INTO consensus
            (id, subject_id, type_id, object_id,
             rating, rd, volatility, witness_count, last_observed_at)
        SELECT b.id, b.s, v_type, b.o,
               (b.fresh).rating, (b.fresh).rd, (b.fresh).volatility,
               b.games, b.ts
        FROM (
            SELECT consensus_id(u.s, v_type, u.o) AS id,
                   u.s, u.o, u.phi, u.games, u.sum, u.ts,
                   laplace_glicko2_accumulate_games(
                       glicko2_neutral_mu(), glicko2_initial_rd(),
                       glicko2_initial_volatility(),
                       glicko2_neutral_mu(), u.phi, u.games, u.sum,
                       glicko2_tau()) AS fresh
            FROM unnest(p_subjects, p_types, p_objects,
                        p_phis, p_games, p_sums, p_ts)
                 AS u(s, t, o, phi, games, sum, ts)
            WHERE u.t = v_type
        ) b
        WHERE NOT EXISTS (
            SELECT 1 FROM consensus c
            WHERE c.type_id = v_type
              AND c.subject_id = b.s
              AND c.id = b.id);
        GET DIAGNOSTICS v_rc = ROW_COUNT;
        affected := affected + v_rc;
    END LOOP;
    RETURN affected;
END $$;

-- ==== v2: unnest ONCE into an indexed temp table; loop reads only its slice ====
CREATE OR REPLACE FUNCTION laplace.consensus_upsert_v2(
    p_subjects bytea[], p_types bytea[], p_objects bytea[],
    p_phis bigint[], p_games bigint[], p_sums bigint[], p_ts timestamptz[])
RETURNS bigint LANGUAGE plpgsql VOLATILE
SET search_path = laplace, public AS $$
DECLARE
    affected bigint := 0;
    v_rc     bigint;
    v_type   bytea;
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS _cu_batch (
        id bytea, s bytea, t bytea, o bytea,
        phi bigint, games bigint, sum bigint, ts timestamptz,
        fresh_rating double precision, fresh_rd double precision, fresh_vol double precision
    ) ON COMMIT DELETE ROWS;
    TRUNCATE _cu_batch;

    INSERT INTO _cu_batch
    SELECT consensus_id(u.s, u.t, u.o), u.s, u.t, u.o,
           u.phi, u.games, u.sum, u.ts,
           f.rating, f.rd, f.volatility
    FROM unnest(p_subjects, p_types, p_objects, p_phis, p_games, p_sums, p_ts)
         AS u(s, t, o, phi, games, sum, ts)
    CROSS JOIN LATERAL laplace_glicko2_accumulate_games(
             glicko2_neutral_mu(), glicko2_initial_rd(), glicko2_initial_volatility(),
             glicko2_neutral_mu(), u.phi, u.games, u.sum, glicko2_tau()) AS f;

    IF EXISTS (SELECT 1 FROM _cu_batch GROUP BY id HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'duplicate cell' USING ERRCODE = 'cardinality_violation';
    END IF;

    CREATE INDEX IF NOT EXISTS _cu_batch_t ON _cu_batch (t);
    ANALYZE _cu_batch;

    FOR v_type IN SELECT DISTINCT t FROM _cu_batch ORDER BY t LOOP
        UPDATE consensus c SET
            (rating, rd, volatility) = (
                SELECT r.rating, r.rd, r.volatility
                FROM laplace_glicko2_accumulate_games(
                        c.rating, c.rd, c.volatility, glicko2_neutral_mu(),
                        b.phi, b.games, b.sum, glicko2_tau()) AS r),
            witness_count    = c.witness_count + b.games,
            last_observed_at = GREATEST(c.last_observed_at, b.ts)
        FROM _cu_batch b
        WHERE b.t = v_type
          AND c.type_id = v_type
          AND c.subject_id = b.s
          AND c.id = b.id;
        GET DIAGNOSTICS v_rc = ROW_COUNT;
        affected := affected + v_rc;

        INSERT INTO consensus
            (id, subject_id, type_id, object_id,
             rating, rd, volatility, witness_count, last_observed_at)
        SELECT b.id, b.s, v_type, b.o,
               b.fresh_rating, b.fresh_rd, b.fresh_vol, b.games, b.ts
        FROM _cu_batch b
        WHERE b.t = v_type
          AND NOT EXISTS (
            SELECT 1 FROM consensus c
            WHERE c.type_id = v_type
              AND c.subject_id = b.s
              AND c.id = b.id);
        GET DIAGNOSTICS v_rc = ROW_COUNT;
        affected := affected + v_rc;
    END LOOP;
    RETURN affected;
END $$;

-- ==== synthetic batch: 200k unique cells across 10 real relation types ====
CREATE TEMP TABLE gen AS
SELECT decode(md5(i::text), 'hex') AS s,
       (ARRAY[
         '\x1d0f589ecbb06a33e2a18e4f0dcf6fb0','\x402001019b7e964d3cf0ef7532de16bb',
         '\xc9490ac67209d8d3efd7993a24d88102','\xf06c77af020e2f5b22a4fc6f03076e1f',
         '\x46582b42dfb9ef1dc36c4ada24200b56','\x5c90e904dd802ac50a07945b1ac1e2c5',
         '\x265912118b5441ae9d3e686c09ad01dc','\x512196c160061a98c0676206d1b052fd',
         '\x939f7e2fd91ba7cb15723f12a66bafdf','\x89a247ca591ad90596a3cb6114e256db'
       ]::bytea[])[1 + (i % 10)] AS t,
       decode(md5('o' || i::text), 'hex') AS o,
       1000000::bigint AS phi, 3::bigint AS games, 4::bigint AS sum,
       now()::timestamptz AS ts
FROM generate_series(1, 200000) i;

\timing on
\echo ==== INSERT PATH: v1 (current main) ====
BEGIN;
SELECT laplace.consensus_upsert_v1(array_agg(s), array_agg(t), array_agg(o),
                                   array_agg(phi), array_agg(games), array_agg(sum),
                                   array_agg(ts)) AS v1_insert FROM gen;
ROLLBACK;

\echo ==== INSERT PATH: v2 (batch-once) ====
BEGIN;
SELECT laplace.consensus_upsert_v2(array_agg(s), array_agg(t), array_agg(o),
                                   array_agg(phi), array_agg(games), array_agg(sum),
                                   array_agg(ts)) AS v2_insert FROM gen;
ROLLBACK;

\echo ==== seed committed rows for update path (v2) ====
SELECT laplace.consensus_upsert_v2(array_agg(s), array_agg(t), array_agg(o),
                                   array_agg(phi), array_agg(games), array_agg(sum),
                                   array_agg(ts)) AS committed FROM gen;

\echo ==== UPDATE PATH: v1 ====
BEGIN;
SELECT laplace.consensus_upsert_v1(array_agg(s), array_agg(t), array_agg(o),
                                   array_agg(phi), array_agg(games), array_agg(sum),
                                   array_agg(ts)) AS v1_update FROM gen;
ROLLBACK;

\echo ==== UPDATE PATH: v2 ====
BEGIN;
SELECT laplace.consensus_upsert_v2(array_agg(s), array_agg(t), array_agg(o),
                                   array_agg(phi), array_agg(games), array_agg(sum),
                                   array_agg(ts)) AS v2_update FROM gen;
ROLLBACK;
