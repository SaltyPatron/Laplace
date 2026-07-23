BEGIN;
SET search_path = laplace, public;

-- chess_moves / chess_player_moves / typed consensus_by_ids: the chess read
-- surface. Synthetic rows only — one position with three rated continuations
-- and two provenanced games (one as White, one as Black) for a player.
DO $$
DECLARE
    type_t   bytea := laplace_hash128_blake3('Type');
    mv       bytea := relation_type_id('MOVE');
    hw       bytea := relation_type_id('HAS_WHITE');
    hb       bytea := relation_type_id('HAS_BLACK');
    src      bytea := laplace_hash128_blake3('test/chess/source');
    pos      bytea := laplace_hash128_blake3('test/chess/pos');
    n_strong bytea := laplace_hash128_blake3('test/chess/next_strong');
    n_mid    bytea := laplace_hash128_blake3('test/chess/next_mid');
    n_thin   bytea := laplace_hash128_blake3('test/chess/next_thin');
    player   bytea := laplace_hash128_blake3('test/chess/player');
    rival    bytea := laplace_hash128_blake3('test/chess/rival');
    g_white  bytea := laplace_hash128_blake3('test/chess/game_as_white');
    g_black  bytea := laplace_hash128_blake3('test/chess/game_as_black');
    c_strong bytea := laplace_hash128_blake3('test/chess/c_strong');
    c_mid    bytea := laplace_hash128_blake3('test/chess/c_mid');
    c_thin   bytea := laplace_hash128_blake3('test/chess/c_thin');
    got      bytea[];
    n        bigint;
    s        double precision;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (pos, 0, type_t, src), (n_strong, 0, type_t, src),
        (n_mid, 0, type_t, src), (n_thin, 0, type_t, src),
        (player, 0, type_t, src), (rival, 0, type_t, src),
        (g_white, 0, type_t, src), (g_black, 0, type_t, src);

    -- eff_mu = rating - 2*rd:
    --   strong: 1600e9 - 2*50e9  = 1500e9   (ranked 1st)
    --   mid:    1550e9 - 2*40e9  = 1470e9   (ranked 2nd)
    --   thin:   1500e9 - 2*200e9 = 1100e9   (ranked 3rd — wide RD sinks it)
    INSERT INTO consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
    VALUES
        (c_strong, pos, mv, n_strong, 1600000000000, 50000000000, 60000000, 100, now()),
        (c_mid,    pos, mv, n_mid,    1550000000000, 40000000000, 60000000, 50, now()),
        (c_thin,   pos, mv, n_thin,   1500000000000, 200000000000, 60000000, 1, now());

    -- MOVE evidence with per-game context; game headers bind games to players.
    INSERT INTO attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count)
    VALUES
        (laplace_hash128_blake3('test/chess/ev_strong'), pos, mv, n_strong, src, g_white, 2, now(), 3),
        (laplace_hash128_blake3('test/chess/ev_mid'),    pos, mv, n_mid,    src, g_black, 0, now(), 1),
        (laplace_hash128_blake3('test/chess/hw1'), g_white, hw, player, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('test/chess/hb1'), g_white, hb, rival,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('test/chess/hw2'), g_black, hw, rival,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('test/chess/hb2'), g_black, hb, player, src, NULL, 2, now(), 1);

    -- chess_moves: eff_mu ranking, full and LIMITed.
    SELECT array_agg(next_position ORDER BY ord) INTO got
    FROM (SELECT next_position, row_number() OVER () AS ord FROM chess_moves(pos)) q;
    IF got <> ARRAY[n_strong, n_mid, n_thin] THEN
        RAISE EXCEPTION 'FAIL: chess_moves ranking wrong: %', got;
    END IF;

    SELECT count(*) INTO n FROM chess_moves(pos, 2);
    IF n <> 2 THEN RAISE EXCEPTION 'FAIL: chess_moves LIMIT 2 returned % rows', n; END IF;

    -- chess_player_moves: only the game where the player held the queried color.
    SELECT count(*) INTO n FROM chess_player_moves(pos, player, true);
    IF n <> 1 THEN RAISE EXCEPTION 'FAIL: player-as-white expected 1 row, got %', n; END IF;
    SELECT games, score INTO n, s FROM chess_player_moves(pos, player, true);
    IF n <> 3 OR s <> 1.0 THEN
        RAISE EXCEPTION 'FAIL: player-as-white expected games=3 score=1.0, got %/%', n, s;
    END IF;

    SELECT games, score INTO n, s FROM chess_player_moves(pos, player, false);
    IF n <> 1 OR s <> 0.0 THEN
        RAISE EXCEPTION 'FAIL: player-as-black expected games=1 score=0.0, got %/%', n, s;
    END IF;

    -- typed consensus_by_ids prunes to the passed relation partition.
    SELECT count(*) INTO n FROM consensus_by_ids(ARRAY[c_strong, c_mid, c_thin], mv);
    IF n <> 3 THEN RAISE EXCEPTION 'FAIL: typed consensus_by_ids(MOVE) got % rows', n; END IF;
    SELECT count(*) INTO n FROM consensus_by_ids(ARRAY[c_strong, c_mid, c_thin], hw);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: typed consensus_by_ids(HAS_WHITE) got % rows', n; END IF;

    RAISE NOTICE '✓ chess_read: chess_moves ranks by eff_mu, player repertoire follows color+context, typed consensus_by_ids prunes';
END $$;

ROLLBACK;
