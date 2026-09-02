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

-- Simulate evidence committed while the post-evidence fold failed: consensus
-- exists but is one witness and one timestamp behind. No opponent rating is zero,
-- so the historical repair's old early-return path would have done nothing.
INSERT INTO laplace.consensus
    (id, subject_id, type_id, object_id, rating, rd, volatility,
     witness_count, last_observed_at)
SELECT laplace.consensus_id(player_id, outcome_t, result_id),
       player_id, outcome_t, result_id,
       1500000000000, 350000000000, 60000000,
       1, '2026-01-01 00:00:00+00'::timestamptz
FROM _repair_ids;

CALL chess.repair_player_ratings(
    laplace.relation_type_id('OUTCOME'),
    laplace.relation_type_id('PLAYED_BY'),
    laplace.relation_type_id('HAS_RATING'));

DO $$
DECLARE
    r record;
BEGIN
    SELECT c.* INTO STRICT r
    FROM _repair_ids i
    JOIN laplace.consensus c
      ON c.id = laplace.consensus_id(i.player_id, i.outcome_t, i.result_id)
     AND c.type_id = i.outcome_t
     AND c.subject_id = i.player_id;

    IF r.witness_count <> 2 THEN
        RAISE EXCEPTION 'FAIL: fold-debt repair witness_count %, expected 2', r.witness_count;
    END IF;
    IF r.last_observed_at <> '2026-01-02 03:04:05+00'::timestamptz THEN
        RAISE EXCEPTION 'FAIL: fold-debt repair timestamp %', r.last_observed_at;
    END IF;
    IF r.rd <= 0 OR r.volatility <= 0 THEN
        RAISE EXCEPTION 'FAIL: fold-debt repair emitted illegal Glicko state rd=% volatility=%',
            r.rd, r.volatility;
    END IF;
    RAISE NOTICE 'chess fold debt repaired from durable evidence';
END $$;

CREATE TEMP TABLE _repair_snapshot AS
SELECT c.rating, c.rd, c.volatility, c.witness_count, c.last_observed_at
FROM _repair_ids i
JOIN laplace.consensus c
  ON c.id = laplace.consensus_id(i.player_id, i.outcome_t, i.result_id)
 AND c.type_id = i.outcome_t
 AND c.subject_id = i.player_id;

CALL chess.repair_player_ratings(
    laplace.relation_type_id('OUTCOME'),
    laplace.relation_type_id('PLAYED_BY'),
    laplace.relation_type_id('HAS_RATING'));

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
        RAISE EXCEPTION 'FAIL: second fold-debt repair changed an already repaired cell';
    END IF;
    RAISE NOTICE 'chess fold debt repair replay is idempotent';
END $$;
