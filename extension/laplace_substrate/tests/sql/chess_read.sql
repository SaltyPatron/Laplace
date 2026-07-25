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

-- The player/game read surface: name folding, the W/L/D law, and the four reads a
-- career page is built from. Four synthetic games between three players, one of
-- them deliberately left unscored so abstention can be told apart from a draw.
DO $$
DECLARE
    type_t  bytea := laplace_hash128_blake3('Type');
    hw      bytea := relation_type_id('HAS_WHITE');
    hb      bytea := relation_type_id('HAS_BLACK');
    hr      bytea := relation_type_id('HAS_RESULT');
    hrat    bytea := relation_type_id('HAS_RATING');
    src     bytea := laplace_hash128_blake3('test/chess2/source');
    tal     bytea := chess_player_id('Tal, Mikhail');
    botv    bytea := chess_player_id('Botvinnik, Mikhail');
    spas    bytea := chess_player_id('Spassky, Boris');
    g1      bytea := laplace_hash128_blake3('test/chess2/g1');
    g2      bytea := laplace_hash128_blake3('test/chess2/g2');
    g3      bytea := laplace_hash128_blake3('test/chess2/g3');
    g4      bytea := laplace_hash128_blake3('test/chess2/g4');
    r_white bytea := word_id('1-0');
    r_black bytea := word_id('0-1');
    r_draw  bytea := word_id('1/2-1/2');
    r_junk  bytea := laplace_hash128_blake3('test/chess2/unparseable_result');
    elo     bytea := laplace_hash128_blake3('test/chess2/elo_tag');
    n       bigint;
    w       bigint;
    d       bigint;
    l       bigint;
    u       bigint;
    sc      double precision;
    b       boolean;
    got     bytea;
    txt     text;
BEGIN
    -- chess_player_id: the decomposer's own folding, reproduced. "Last, First"
    -- flips, case and punctuation and repeated spaces all fold away — so every
    -- way a source might spell one man lands on ONE content address.
    IF chess_player_id('Tal, Mikhail') <> chess_player_id('Mikhail Tal') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id did not flip "Last, First"';
    END IF;
    IF chess_player_id('Tal, Mikhail') <> chess_player_id('  TAL ,   mikhail  ') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id is not case/space insensitive';
    END IF;
    IF chess_player_id('Tal, Mikhail') <> chess_player_id('Tal, Mikhail.') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id did not fold trailing punctuation';
    END IF;
    IF chess_player_id('Tal, Mikhail') = chess_player_id('Tal, Michael') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id collapsed two different names';
    END IF;

    -- chess_outcome: the whole W/L/D law, both colours, all three tokens. This is
    -- the one place a result token becomes an outcome, so it is pinned exhaustively.
    IF chess_outcome(true,  r_white) <> 2 THEN RAISE EXCEPTION 'FAIL: white in 1-0 is not a win'; END IF;
    IF chess_outcome(false, r_white) <> 0 THEN RAISE EXCEPTION 'FAIL: black in 1-0 is not a loss'; END IF;
    IF chess_outcome(true,  r_black) <> 0 THEN RAISE EXCEPTION 'FAIL: white in 0-1 is not a loss'; END IF;
    IF chess_outcome(false, r_black) <> 2 THEN RAISE EXCEPTION 'FAIL: black in 0-1 is not a win'; END IF;
    IF chess_outcome(true,  r_draw)  <> 1 THEN RAISE EXCEPTION 'FAIL: white in 1/2-1/2 is not a draw'; END IF;
    IF chess_outcome(false, r_draw)  <> 1 THEN RAISE EXCEPTION 'FAIL: black in 1/2-1/2 is not a draw'; END IF;
    -- an unreadable result token ABSTAINS; it must never be scored as a draw
    IF chess_outcome(true, r_junk) IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: an unrecognised result token was scored instead of abstaining';
    END IF;
    IF chess_outcome(true, NULL) IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: a missing result was scored instead of abstaining';
    END IF;

    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (tal, 0, type_t, src), (botv, 0, type_t, src), (spas, 0, type_t, src),
        (g1, 0, type_t, src), (g2, 0, type_t, src),
        (g3, 0, type_t, src), (g4, 0, type_t, src),
        (r_white, 0, type_t, src), (r_black, 0, type_t, src), (r_draw, 0, type_t, src),
        (elo, 0, type_t, src);

    -- g1 Tal(W) beat Botvinnik   g2 Botvinnik(W) beat Tal
    -- g3 Tal(W) drew Spassky     g4 Tal(W) v Botvinnik, never scored
    INSERT INTO attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count)
    VALUES
        (laplace_hash128_blake3('t2/g1w'), g1, hw, tal,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g1b'), g1, hb, botv, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g1r'), g1, hr, r_white, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g2w'), g2, hw, botv, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g2b'), g2, hb, tal,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g2r'), g2, hr, r_white, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g3w'), g3, hw, tal,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g3b'), g3, hb, spas, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g3r'), g3, hr, r_draw, src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g4w'), g4, hw, tal,  src, NULL, 2, now(), 1),
        (laplace_hash128_blake3('t2/g4b'), g4, hb, botv, src, NULL, 2, now(), 1),
        -- an Elo tag whose surface cannot be rendered back to digits
        (laplace_hash128_blake3('t2/rat'), tal, hrat, elo, src, g1, 2, now(), 1);

    -- chess_player_record: the career total. 4 games — one won, one lost, one
    -- drawn, one the source never scored. score is over SCORED games only:
    -- (1 win + 1/2 draw) / 3 = 0.5. Counting the unscored game as a loss would
    -- give 0.375, and silently dropping it from `games` would hide it entirely.
    SELECT games, wins, draws, losses, unscored, score
      INTO n, w, d, l, u, sc
      FROM chess_player_record(tal) WHERE as_white IS NULL;
    IF n <> 4 OR w <> 1 OR d <> 1 OR l <> 1 OR u <> 1 THEN
        RAISE EXCEPTION 'FAIL: Tal career expected 4/1w/1d/1l/1u, got %/%/%/%/%', n, w, d, l, u;
    END IF;
    IF sc IS DISTINCT FROM 0.5 THEN
        RAISE EXCEPTION 'FAIL: score must be over scored games only, got %', sc;
    END IF;

    -- the colour splits come from the same pass, so they must reconcile exactly
    SELECT sum(games), sum(wins), sum(draws), sum(losses), sum(unscored)
      INTO n, w, d, l, u
      FROM chess_player_record(tal) WHERE as_white IS NOT NULL;
    IF n <> 4 OR w <> 1 OR d <> 1 OR l <> 1 OR u <> 1 THEN
        RAISE EXCEPTION 'FAIL: colour splits do not reconcile with the total: %/%/%/%/%', n, w, d, l, u;
    END IF;
    SELECT games INTO n FROM chess_player_record(tal) WHERE as_white;
    IF n <> 3 THEN RAISE EXCEPTION 'FAIL: Tal as White expected 3 games, got %', n; END IF;
    SELECT games, losses INTO n, l FROM chess_player_record(tal) WHERE NOT as_white;
    IF n <> 1 OR l <> 1 THEN
        RAISE EXCEPTION 'FAIL: Tal as Black expected 1 game / 1 loss, got %/%', n, l;
    END IF;

    -- a player nobody has witnessed has an empty record, not an error
    SELECT games INTO n FROM chess_player_record(chess_player_id('Nobody At All'))
     WHERE as_white IS NULL;
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: unwitnessed player reported % games', n; END IF;

    -- chess_player_games: the log behind the record. The Black game is the loss,
    -- and it names the opponent from the far side of the same game.
    SELECT count(*) INTO n FROM chess_player_games(tal, 25, 0);
    IF n <> 4 THEN RAISE EXCEPTION 'FAIL: Tal game log expected 4 rows, got %', n; END IF;
    SELECT as_white, outcome, opponent_id INTO b, l, got
      FROM chess_player_games(tal, 25, 0) WHERE game_id = g2;
    IF b OR l <> 0 OR got <> botv THEN
        RAISE EXCEPTION 'FAIL: g2 should be Tal as Black, lost, to Botvinnik — got %/%/%', b, l, got;
    END IF;
    -- the unscored game is LISTED, with an abstaining outcome
    SELECT outcome INTO l FROM chess_player_games(tal, 25, 0) WHERE game_id = g4;
    IF l IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: the unscored game was given outcome % instead of abstaining', l;
    END IF;
    -- with no renderable name, the opponent falls back to its id rather than blank
    SELECT opponent INTO txt FROM chess_player_games(tal, 25, 0) WHERE game_id = g1;
    IF txt IS NULL OR txt = '' THEN
        RAISE EXCEPTION 'FAIL: nameless opponent rendered empty instead of falling back to hex';
    END IF;
    -- paging
    SELECT count(*) INTO n FROM chess_player_games(tal, 2, 0);
    IF n <> 2 THEN RAISE EXCEPTION 'FAIL: chess_player_games LIMIT 2 got % rows', n; END IF;
    SELECT count(*) INTO n FROM chess_player_games(tal, 25, 4);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: paging past the end got % rows', n; END IF;

    -- chess_opponents: the same four games regrouped by who was opposite. The
    -- totals must equal the career total, or the two views disagree about one man.
    SELECT sum(games) INTO n FROM chess_opponents(tal, 25);
    IF n <> 4 THEN RAISE EXCEPTION 'FAIL: head-to-head totals % games, career has 4', n; END IF;
    SELECT games, wins, draws, losses, unscored INTO n, w, d, l, u
      FROM chess_opponents(tal, 25) WHERE opponent_id = botv;
    IF n <> 3 OR w <> 1 OR d <> 0 OR l <> 1 OR u <> 1 THEN
        RAISE EXCEPTION 'FAIL: Tal v Botvinnik expected 3/1w/0d/1l/1u, got %/%/%/%/%', n, w, d, l, u;
    END IF;
    SELECT games, draws INTO n, d FROM chess_opponents(tal, 25) WHERE opponent_id = spas;
    IF n <> 1 OR d <> 1 THEN
        RAISE EXCEPTION 'FAIL: Tal v Spassky expected one drawn game, got %/%', n, d;
    END IF;

    -- chess_game: the headers, both sides followable back to their careers
    SELECT white_id INTO got FROM chess_game(g1);
    IF got <> tal THEN RAISE EXCEPTION 'FAIL: g1 White should be Tal'; END IF;
    SELECT black_id INTO got FROM chess_game(g1);
    IF got <> botv THEN RAISE EXCEPTION 'FAIL: g1 Black should be Botvinnik'; END IF;
    SELECT count(*) INTO n FROM chess_game(laplace_hash128_blake3('test/chess2/no_such_game'));
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: an unwitnessed game returned % rows', n; END IF;

    -- chess_player_ratings: an Elo tag that will not render back to digits is an
    -- unreadable witness and is SKIPPED, never coerced to a number.
    SELECT count(*) INTO n FROM chess_player_ratings(tal);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: an unparseable rating tag was coerced, got % rows', n; END IF;

    -- chess_leaderboard: ranked by games witnessed, no floors — Spassky's single
    -- game keeps him on the board, just last.
    SELECT player_id INTO got FROM chess_leaderboard(10) WHERE rank = 1;
    IF got <> tal THEN RAISE EXCEPTION 'FAIL: leaderboard rank 1 should be Tal (4 games)'; END IF;
    SELECT games INTO n FROM chess_leaderboard(10) WHERE player_id = spas;
    IF n <> 1 THEN RAISE EXCEPTION 'FAIL: a one-game player must still appear, got %', n; END IF;
    SELECT count(*) INTO n FROM chess_leaderboard(2);
    IF n <> 2 THEN RAISE EXCEPTION 'FAIL: chess_leaderboard LIMIT 2 got % rows', n; END IF;
    -- names abstain to hex rather than coming back blank
    SELECT name INTO txt FROM chess_leaderboard(10) WHERE rank = 1;
    IF txt IS NULL OR txt = '' THEN
        RAISE EXCEPTION 'FAIL: nameless player rendered blank instead of falling back to hex';
    END IF;

    RAISE NOTICE '✓ chess_read: name folding is content-addressed, W/L/D abstains on unscored games, career/log/head-to-head/leaderboard all reconcile';
END $$;

ROLLBACK;
