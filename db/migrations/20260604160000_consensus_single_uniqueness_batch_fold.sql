-- 20260604160000_consensus_single_uniqueness_batch_fold.sql
--
-- Converges EXISTING databases to the 2026-06-04 consensus corrections
-- (schema-of-record: extension/laplace_substrate/sql/06_glicko2.sql.in +
-- 13_consensus.sql.in, changed in the same commit; fresh installs get all of
-- this from CREATE EXTENSION).
--
-- 1. Uniqueness on consensus is the content-addressed PK alone: id IS
--    BLAKE3(subject ‖ kind ‖ object|zero16) (consensus_id), so the composite
--    UNIQUE (subject, kind, object) restated the same invariant at ~144 B/row
--    — ~20 GB of duplicate structure at model scale (153M relations per
--    1B-param model), maintained on every period-fold upsert. Same correction
--    as attestations (20260604070000).
-- 2. consensus_subject_btree was a pure prefix of consensus_subject_kind_btree.
-- 3. The period fold replayed every game as an executor row
--    (CROSS JOIN LATERAL generate_series → 1.1e9 rows for TinyLlama's 153M
--    relations; measured 182 min) and probed the prior TWICE. The batch entry
--    laplace_glicko2_accumulate_games(n, Σs) builds the identical observation
--    multiset inside the C kernel — bit-identical (pinned by regress
--    glicko2_aggregate Vector 6) — one call and one prior probe per relation.

ALTER TABLE laplace.consensus
    DROP CONSTRAINT IF EXISTS consensus_subject_id_kind_id_object_id_key;

DROP INDEX IF EXISTS laplace.consensus_subject_btree;

-- The batch period entry (C symbol ships in the laplace_substrate module —
-- requires the 2026-06-04 .so already installed cluster-wide via `just install`).
CREATE OR REPLACE FUNCTION laplace.laplace_glicko2_accumulate_games(
    prior_rating     bigint,
    prior_rd         bigint,
    prior_volatility bigint,
    opponent_rating  bigint,
    opponent_rd      bigint,
    games            bigint,
    sum_score        bigint,
    tau              bigint
) RETURNS laplace.laplace_glicko2_result
    AS '$libdir/laplace_substrate', 'pg_laplace_glicko2_accumulate_games'
    LANGUAGE C IMMUTABLE;

COMMENT ON FUNCTION laplace.laplace_glicko2_accumulate_games(bigint, bigint, bigint, bigint, bigint, bigint, bigint, bigint) IS
    'Batch Glicko-2 period update from (games, sum_score) against one opponent: identical observation multiset, identical kernel, one call per relation instead of one aggregate row per game.';

-- The fold, replayless. Body mirrors 13_consensus.sql.in (schema-of-record).
CREATE OR REPLACE FUNCTION laplace.materialize_period_consensus()
    RETURNS bigint LANGUAGE plpgsql VOLATILE
    SET search_path = laplace, public
    SET session_replication_role = replica AS $$
DECLARE
    n_bad bigint;
    n_materialized bigint;
BEGIN
    SELECT count(*) INTO n_bad FROM (
        SELECT 1 FROM consensus_period_staging
        GROUP BY subject_id, kind_id, object_id
        HAVING min(phi) <> max(phi)) bad;
    IF n_bad > 0 THEN
        RAISE EXCEPTION 'accumulation invariant violated: % relation(s) observed with mixed φ within one period', n_bad;
    END IF;

    WITH merged AS (
        SELECT s.subject_id, s.kind_id, s.object_id,
               min(s.phi)               AS phi,
               sum(s.games)::bigint     AS games,
               sum(s.sum_score)::bigint AS sum_score,
               max(s.last_ts)           AS last_ts
        FROM consensus_period_staging s
        GROUP BY s.subject_id, s.kind_id, s.object_id
    )
    INSERT INTO consensus (id, subject_id, kind_id, object_id,
                           rating, rd, volatility, witness_count, last_observed_at)
    SELECT f.cid,
           f.subject_id, f.kind_id, f.object_id,
           (f.acc).rating, (f.acc).rd, (f.acc).volatility,
           f.prior_witnesses + f.games,
           f.last_ts
    FROM (
        SELECT m.subject_id, m.kind_id, m.object_id, m.games, m.last_ts,
               m.cid, COALESCE(c.witness_count, 0) AS prior_witnesses,
               laplace_glicko2_accumulate_games(
                   COALESCE(c.rating,     1500000000000::bigint),
                   COALESCE(c.rd,          350000000000::bigint),
                   COALESCE(c.volatility,     60000000::bigint),
                   1500000000000::bigint,
                   m.phi,
                   m.games,
                   m.sum_score,
                   500000000::bigint
               ) AS acc
        FROM (SELECT mm.*, consensus_id(mm.subject_id, mm.kind_id, mm.object_id) AS cid
              FROM merged mm) m
        LEFT JOIN consensus c ON c.id = m.cid
    ) f
    ON CONFLICT (id) DO UPDATE SET
        rating           = EXCLUDED.rating,
        rd               = EXCLUDED.rd,
        volatility       = EXCLUDED.volatility,
        witness_count    = EXCLUDED.witness_count,
        last_observed_at = EXCLUDED.last_observed_at;
    GET DIAGNOSTICS n_materialized = ROW_COUNT;

    DROP TABLE consensus_period_staging;
    RETURN n_materialized;
END;
$$;
