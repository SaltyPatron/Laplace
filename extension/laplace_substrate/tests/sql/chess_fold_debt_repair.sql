CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

CREATE TEMP TABLE _repair_ids AS
SELECT laplace.relation_type_id('OUTCOME') AS outcome_t,
       laplace.relation_type_id('PLAYED_BY') AS played_t,
       laplace.relation_type_id('HAS_RATING') AS rating_t,
       public.laplace_hash128_blake3('test/chess-fold-debt/player') AS player_id,
       public.laplace_hash128_blake3('test/chess-fold-debt/opponent') AS opponent_id,
       public.laplace_hash128_blake3('test/chess-fold-debt/result') AS result_id,
       public.laplace_hash128_blake3('test/chess-fold-debt/playing') AS playing_id,
       public.laplace_hash128_blake3('test/chess-fold-debt/source') AS source_id;

INSERT INTO laplace.attestations
    (id, subject_id, type_id, object_id, source_id, context_id, outcome,
     last_observed_at, observation_count, sum_score_fp1e9,
     opponent_rd_fp1e9, opponent_rating_fp1e9, highway_mask)
SELECT public.laplace_hash128_blake3('test/chess-fold-debt/outcome-evidence'),
       player_id, outcome_t, result_id, source_id, playing_id, 2,
       '2026-01-02 03:04:05+00'::timestamptz, 2, 1000000000,
       100000000000, 1600000000000, NULL
FROM _repair_ids;

INSERT INTO laplace.attestations
    (id, subject_id, type_id, object_id, source_id, context_id, outcome,
     last_observed_at, observation_count, sum_score_fp1e9,
     opponent_rd_fp1e9, opponent_rating_fp1e9, highway_mask)
SELECT public.laplace_hash128_blake3('test/chess-fold-debt/played-evidence'),
       player_id, played_t, opponent_id, source_id, playing_id, 2,
       '2026-01-02 03:04:05+00'::timestamptz, 1, 500000000,
       100000000000, 1600000000000, NULL
FROM _repair_ids;

-- Reproduce the live warehouse failure class: the derived row has the SAME
-- witness count and timestamp as durable evidence, but its Glicko carrier is
-- wildly corrupt. Cardinality/time-only debt detection cannot see this state.
INSERT INTO laplace.consensus
    (id, subject_id, type_id, object_id, rating, rd, volatility,
     witness_count, last_observed_at)
SELECT laplace.consensus_id(player_id, outcome_t, result_id),
       player_id, outcome_t, result_id,
       10398275800000000, 350000000000, 60000000,
       2, '2026-01-02 03:04:05+00'::timestamptz
FROM _repair_ids;

-- Historical repair is explicit and bounded. Deployment/steady-state operation
-- must not invoke or schedule it.
CALL chess.repair_player_ratings_batch(
    laplace.relation_type_id('OUTCOME'),
    laplace.relation_type_id('PLAYED_BY'),
    laplace.relation_type_id('HAS_RATING'),
    ARRAY[public.laplace_hash128_blake3('test/chess-fold-debt/player')]::bytea[]);

DO $$
DECLARE
    r record;
    expected_rating bigint;
    expected_rd bigint;
    expected_volatility bigint;
    expected_witnesses bigint;
BEGIN
    SELECT c.* INTO STRICT r
    FROM _repair_ids i
    JOIN laplace.consensus c
      ON c.id = laplace.consensus_id(i.player_id, i.outcome_t, i.result_id)
     AND c.type_id = i.outcome_t
     AND c.subject_id = i.player_id;

    SELECT (q.acc).rating, (q.acc).rd, (q.acc).volatility, (q.acc).witness_count
    INTO STRICT expected_rating, expected_rd, expected_volatility, expected_witnesses
    FROM (
        SELECT laplace.consensus_fold(
                   false, NULL::bigint, NULL::bigint, NULL::bigint,
                   e.opponent_rating_fp1e9,
                   e.opponent_rd_fp1e9,
                   GREATEST(e.observation_count, 1),
                   e.sum_score_fp1e9,
                   consensus.glicko2_tau()
                   ORDER BY e.last_observed_at, e.id) AS acc
        FROM _repair_ids i
        JOIN laplace.attestations e
          ON e.subject_id = i.player_id
         AND e.type_id = i.outcome_t
         AND e.object_id = i.result_id
    ) q;

    IF ROW(r.rating, r.rd, r.volatility, r.witness_count)
       IS DISTINCT FROM
       ROW(expected_rating, expected_rd, expected_volatility, expected_witnesses) THEN
        RAISE EXCEPTION 'FAIL: count-complete corrupt consensus was not rebuilt exactly from evidence';
    END IF;
    IF r.rating = 10398275800000000 THEN
        RAISE EXCEPTION 'FAIL: runaway rating survived batched chess refold';
    END IF;
    IF r.last_observed_at <> '2026-01-02 03:04:05+00'::timestamptz THEN
        RAISE EXCEPTION 'FAIL: chess rating repair timestamp %', r.last_observed_at;
    END IF;
    IF r.rd <= 0 OR r.volatility <= 0 THEN
        RAISE EXCEPTION 'FAIL: chess rating repair emitted illegal Glicko state rd=% volatility=%',
            r.rd, r.volatility;
    END IF;
    RAISE NOTICE 'count-complete runaway chess rating rebuilt exactly from durable evidence';
END $$;

CREATE TEMP TABLE _repair_snapshot AS
SELECT c.rating, c.rd, c.volatility, c.witness_count, c.last_observed_at
FROM _repair_ids i
JOIN laplace.consensus c
  ON c.id = laplace.consensus_id(i.player_id, i.outcome_t, i.result_id)
 AND c.type_id = i.outcome_t
 AND c.subject_id = i.player_id;

CALL chess.repair_player_ratings_batch(
    laplace.relation_type_id('OUTCOME'),
    laplace.relation_type_id('PLAYED_BY'),
    laplace.relation_type_id('HAS_RATING'),
    ARRAY[public.laplace_hash128_blake3('test/chess-fold-debt/player')]::bytea[]);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM _repair_snapshot s, _repair_ids i
        JOIN laplace.consensus c
          ON c.id = laplace.consensus_id(i.player_id, i.outcome_t, i.result_id)
         AND c.type_id = i.outcome_t
         AND c.subject_id = i.player_id
        WHERE ROW(c.rating, c.rd, c.volatility, c.witness_count, c.last_observed_at)
              IS DISTINCT FROM
              ROW(s.rating, s.rd, s.volatility, s.witness_count, s.last_observed_at)
    ) THEN
        RAISE EXCEPTION 'FAIL: second chess rating repair batch changed an already repaired cell';
    END IF;
    RAISE NOTICE 'batched chess rating repair replay is idempotent';
END $$;
