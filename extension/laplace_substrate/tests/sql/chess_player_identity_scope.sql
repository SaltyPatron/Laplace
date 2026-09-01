CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

BEGIN;

-- #1405: an already-present playing can still carry a historical player-id
-- encoding. Replaying that PGN must not double testimony just to repair the
-- reference, so exact career reads admit only the deterministic ChessPgn
-- legacy bridge emitted by ChessPgnDecomposer.EmitPlayer.
DO $$
DECLARE
    canonical bytea := decode(repeat('c1', 16), 'hex');
    legacy    bytea := decode(repeat('c2', 16), 'hex');
    line_old  bytea := decode(repeat('c3', 16), 'hex');
    play_old  bytea := decode(repeat('c4', 16), 'hex');
    line_new  bytea := decode(repeat('c5', 16), 'hex');
    play_new  bytea := decode(repeat('c6', 16), 'hex');
    src       bytea := laplace.source_id('ChessPgn');
    corr      bytea := laplace.relation_type_id('CORRESPONDS_TO');
    white_t   bytea := laplace.relation_type_id('HAS_WHITE');
    result_t  bytea := laplace.relation_type_id('HAS_RESULT');
    win       bytea := laplace.word_id('1-0');
    n bigint;
BEGIN
    -- CORRESPONDS_TO is symmetric. Store the reverse orientation deliberately
    -- so the reader must honor both indexed directions.
    INSERT INTO laplace.attestations
        (id, subject_id, type_id, object_id, source_id, context_id, outcome,
         last_observed_at, observation_count, sum_score_fp1e9,
         opponent_rd_fp1e9, opponent_rating_fp1e9)
    VALUES
        (decode(repeat('d1', 16), 'hex'), legacy, corr, canonical, src, NULL,
         2, now(), 1, 1000000000, 30000000000, 1500000000000),
        (decode(repeat('d2', 16), 'hex'), line_old, white_t, legacy, src, play_old,
         2, now(), 1, 1000000000, 30000000000, 1500000000000),
        (decode(repeat('d3', 16), 'hex'), line_old, result_t, win, src, play_old,
         2, now(), 1, 1000000000, 30000000000, 1500000000000),
        (decode(repeat('d4', 16), 'hex'), line_new, white_t, canonical, src, play_new,
         2, now(), 1, 1000000000, 30000000000, 1500000000000),
        (decode(repeat('d5', 16), 'hex'), line_new, result_t, win, src, play_new,
         2, now(), 1, 1000000000, 30000000000, 1500000000000)
    ON CONFLICT (id, type_id, subject_id) DO NOTHING;

    SELECT r.games INTO n
    FROM chess.player_record(canonical) r
    WHERE r.as_white IS NULL;
    IF n <> 2 THEN
        RAISE EXCEPTION 'canonical career should include legacy+current games, got %', n;
    END IF;

    SELECT count(*) INTO n FROM chess.player_games(canonical, 25, 0);
    IF n <> 2 THEN
        RAISE EXCEPTION 'canonical game log should include legacy+current games, got %', n;
    END IF;
END $$;

SELECT 'chess player migration scope' AS probe, true AS ok;

ROLLBACK;
