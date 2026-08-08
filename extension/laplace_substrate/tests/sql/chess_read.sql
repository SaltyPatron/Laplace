BEGIN;
SET search_path = laplace, public;

-- chess_moves / chess_player_moves / typed consensus_by_ids: the chess read
-- surface. Synthetic rows only — one position with three rated continuations
-- and two provenanced playings (one as White, one as Black) for a player,
-- staged in the GH #736 line/event shape.
DO $$
DECLARE
    type_t   bytea := laplace_hash128_blake3('Type');
    mv       bytea := relation_type_id('MOVE');
    hw       bytea := relation_type_id('HAS_WHITE');
    hb       bytea := relation_type_id('HAS_BLACK');
    plns     bytea := relation_type_id('PLAYS_LINE');
    src      bytea := laplace_hash128_blake3('test/chess/source');
    pos      bytea := laplace_hash128_blake3('test/chess/pos');
    n_strong bytea := laplace_hash128_blake3('test/chess/next_strong');
    n_mid    bytea := laplace_hash128_blake3('test/chess/next_mid');
    n_thin   bytea := laplace_hash128_blake3('test/chess/next_thin');
    player   bytea := laplace_hash128_blake3('test/chess/player');
    rival    bytea := laplace_hash128_blake3('test/chess/rival');
    g_white  bytea := laplace_hash128_blake3('test/chess/game_as_white');
    g_black  bytea := laplace_hash128_blake3('test/chess/game_as_black');
    l_white  bytea := laplace_hash128_blake3('test/chess/line_as_white');
    l_black  bytea := laplace_hash128_blake3('test/chess/line_as_black');
    c_strong bytea := laplace_hash128_blake3('test/chess/c_strong');
    c_mid    bytea := laplace_hash128_blake3('test/chess/c_mid');
    c_thin   bytea := laplace_hash128_blake3('test/chess/c_thin');
    got      bytea[];
    n        bigint;
    s        double precision;
    s2       double precision;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (pos, 0, type_t, src), (n_strong, 0, type_t, src),
        (n_mid, 0, type_t, src), (n_thin, 0, type_t, src),
        (player, 0, type_t, src), (rival, 0, type_t, src),
        (g_white, 0, type_t, src), (g_black, 0, type_t, src),
        (l_white, 0, type_t, src), (l_black, 0, type_t, src);

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

    -- MOVE evidence rides ctx = the playing-EVENT (GH #736); the colour
    -- headers subject the shared LINE with ctx = that same event, so the
    -- repertoire join threads context equality, never the line subject.
    INSERT INTO attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count,
         sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES
        (laplace_hash128_blake3('test/chess/ev_strong'), pos, mv, n_strong, src, g_white, 2, now(), 3, 3000000000, 30000000000),
        (laplace_hash128_blake3('test/chess/ev_mid'),    pos, mv, n_mid,    src, g_black, 0, now(), 1, 0, 30000000000),
        (laplace_hash128_blake3('test/chess/pl1'), g_white, plns, l_white, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('test/chess/pl2'), g_black, plns, l_black, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('test/chess/hw1'), l_white, hw, player, src, g_white, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('test/chess/hb1'), l_white, hb, rival,  src, g_white, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('test/chess/hw2'), l_black, hw, rival,  src, g_black, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('test/chess/hb2'), l_black, hb, player, src, g_black, 2, now(), 1, 1000000000, 30000000000);

    -- chess_moves: eff_mu ranking, full and LIMITed.
    SELECT array_agg(next_position ORDER BY ord) INTO got
    FROM (SELECT next_position, row_number() OVER () AS ord FROM chess.moves(pos)) q;
    IF got <> ARRAY[n_strong, n_mid, n_thin] THEN
        RAISE EXCEPTION 'FAIL: chess_moves ranking wrong: %', got;
    END IF;

    SELECT count(*) INTO n FROM chess.moves(pos, 2);
    IF n <> 2 THEN RAISE EXCEPTION 'FAIL: chess_moves LIMIT 2 returned % rows', n; END IF;

    -- eff_mu/rd leave chess_moves in DISPLAY units (the eff_mu_display /1e9 round),
    -- never raw fp1e9: the strong continuation is 1500.0 / 50.0, not 1.5e12 / 5e10.
    SELECT q.eff_mu, q.rd INTO s, s2 FROM chess.moves(pos, 1) q;
    IF s <> 1500.0 OR s2 <> 50.0 THEN
        RAISE EXCEPTION 'FAIL: chess_moves must return display-scale eff_mu/rd, got %/%', s, s2;
    END IF;

    -- chess_player_moves: only the playing where the player held the queried color.
    SELECT count(*) INTO n FROM chess.player_moves(pos, player, true);
    IF n <> 1 THEN RAISE EXCEPTION 'FAIL: player-as-white expected 1 row, got %', n; END IF;
    SELECT games, score INTO n, s FROM chess.player_moves(pos, player, true);
    IF n <> 3 OR s <> 1.0 THEN
        RAISE EXCEPTION 'FAIL: player-as-white expected games=3 score=1.0, got %/%', n, s;
    END IF;

    SELECT games, score INTO n, s FROM chess.player_moves(pos, player, false);
    IF n <> 1 OR s <> 0.0 THEN
        RAISE EXCEPTION 'FAIL: player-as-black expected games=1 score=0.0, got %/%', n, s;
    END IF;

    -- typed consensus_by_ids prunes to the passed relation partition.
    SELECT count(*) INTO n FROM consensus.by_ids(ARRAY[c_strong, c_mid, c_thin], mv);
    IF n <> 3 THEN RAISE EXCEPTION 'FAIL: typed consensus.by_ids(MOVE) got % rows', n; END IF;
    SELECT count(*) INTO n FROM consensus.by_ids(ARRAY[c_strong, c_mid, c_thin], hw);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: typed consensus.by_ids(HAS_WHITE) got % rows', n; END IF;

    RAISE NOTICE '✓ chess_read: chess_moves ranks by eff_mu, player repertoire follows color+context, typed consensus_by_ids prunes';
END $$;

-- The player/game read surface across the GH #736 line/event re-key: name
-- folding, the W/L/D law, and the reads a career page is built from. Four
-- synthetic PLAYINGS between three players -- the game CONTENT is a LINE, the
-- playing is an EVENT holding a (event, PLAYS_LINE, line) record edge, and
-- every header subjects on the LINE with context = the event (EmitGame). One
-- playing is deliberately left unscored so abstention can be told apart from a
-- draw, and it SHARES its line with a scored playing so the context threading
-- is what keeps their records apart.
DO $$
DECLARE
    type_t  bytea := laplace_hash128_blake3('Type');
    hw      bytea := relation_type_id('HAS_WHITE');
    hb      bytea := relation_type_id('HAS_BLACK');
    hr      bytea := relation_type_id('HAS_RESULT');
    hrat    bytea := relation_type_id('HAS_RATING');
    mv      bytea := relation_type_id('MOVE');
    plns    bytea := relation_type_id('PLAYS_LINE');
    od      bytea := relation_type_id('ON_DATE');
    hev     bytea := relation_type_id('HAS_EVENT');
    heco    bytea := relation_type_id('HAS_ECO');
    hmt     bytea := relation_type_id('HAS_MOVETEXT');
    mq      bytea := relation_type_id('MOVE_QUALITY');
    outc    bytea := relation_type_id('OUTCOME');
    cres    bytea := entity_type_id('Chess_Result');
    src     bytea := laplace_hash128_blake3('test/chess2/source');
    tal     bytea := chess.player_id('Tal, Mikhail');
    botv    bytea := chess.player_id('Botvinnik, Mikhail');
    spas    bytea := chess.player_id('Spassky, Boris');
    -- playing-events and the lines they play; ln_a is SHARED by e1 and e4
    e1      bytea := laplace_hash128_blake3('test/chess2/e1');
    e2      bytea := laplace_hash128_blake3('test/chess2/e2');
    e3      bytea := laplace_hash128_blake3('test/chess2/e3');
    e4      bytea := laplace_hash128_blake3('test/chess2/e4');
    ln_a    bytea := laplace_hash128_blake3('test/chess2/line_a');
    ln_b    bytea := laplace_hash128_blake3('test/chess2/line_b');
    ln_c    bytea := laplace_hash128_blake3('test/chess2/line_c');
    pos2    bytea := laplace_hash128_blake3('test/chess2/opening_pos');
    mv_w    bytea := laplace_hash128_blake3('test/chess2/mv_w');
    mv_b    bytea := laplace_hash128_blake3('test/chess2/mv_b');
    r_white bytea := word_id('1-0');
    r_black bytea := word_id('0-1');
    r_draw  bytea := word_id('1/2-1/2');
    r_junk  bytea := laplace_hash128_blake3('test/chess2/unparseable_result');
    elo     bytea := laplace_hash128_blake3('test/chess2/elo_tag');
    pos_probe bytea := laplace_hash128_blake3('test/chess2/position_probe');
    pl_p0   bytea := laplace_hash128_blake3('t2/plies/p0');
    pl_p1   bytea := laplace_hash128_blake3('t2/plies/p1');
    pl_p2   bytea := laplace_hash128_blake3('t2/plies/p2');
    pl_line bytea := laplace_hash128_blake3('t2/plies/line');
    pl_event bytea := laplace_hash128_blake3('t2/plies/event');
    -- Header and SAN surfaces. SINGLE CODEPOINTS on purpose: these reads return
    -- REALIZED text, and a tier-0 codepoint renders straight from the perfcache
    -- with no constituents deposited. A multi-codepoint word_id (the result
    -- tokens '1-0'/'1/2-1/2' this fixture used to reuse here) is a composed
    -- entity whose surface needs its constituent chain, which this fixture
    -- never deposits -- so it realized to NULL and `WHERE san IS NOT NULL`
    -- counted 0. Same trick converse.sql uses for word_id('p').
    d1      bytea := word_id('1');
    d2      bytea := word_id('2');
    d3      bytea := word_id('3');
    en      bytea := word_id('e');
    ec      bytea := word_id('C');
    mt      bytea := word_id('m');
    qg      bytea := word_id('g');
    pl_sa   bytea := word_id('a');
    pl_sb   bytea := word_id('b');
    pl_sc   bytea := word_id('c');
    cap_t   bytea := word_id('T');
    tname   bytea := laplace_hash128_blake3('t2/name/tal');
    ids_got bytea[];
    n       bigint;
    w       bigint;
    d       bigint;
    l       bigint;
    u       bigint;
    sc      double precision;
    ra      double precision;
    rdv     double precision;
    mu      double precision;
    b       boolean;
    got     bytea;
    txt     text;
BEGIN
    -- chess_player_id: the decomposer's own folding, reproduced. "Last, First"
    -- flips, case and punctuation and repeated spaces all fold away — so every
    -- way a source might spell one man lands on ONE content address.
    IF chess.player_id('Tal, Mikhail') <> chess.player_id('Mikhail Tal') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id did not flip "Last, First"';
    END IF;
    IF chess.player_id('Tal, Mikhail') <> chess.player_id('  TAL ,   mikhail  ') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id is not case/space insensitive';
    END IF;
    IF chess.player_id('Tal, Mikhail') <> chess.player_id('Tal, Mikhail.') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id did not fold trailing punctuation';
    END IF;
    IF chess.player_id('Tal, Mikhail') = chess.player_id('Tal, Michael') THEN
        RAISE EXCEPTION 'FAIL: chess_player_id collapsed two different names';
    END IF;

    -- chess_outcome: the whole W/L/D law, both colours, all three tokens. This is
    -- the one place a result token becomes an outcome, so it is pinned exhaustively.
    IF chess.outcome(true,  r_white) <> 2 THEN RAISE EXCEPTION 'FAIL: white in 1-0 is not a win'; END IF;
    IF chess.outcome(false, r_white) <> 0 THEN RAISE EXCEPTION 'FAIL: black in 1-0 is not a loss'; END IF;
    IF chess.outcome(true,  r_black) <> 0 THEN RAISE EXCEPTION 'FAIL: white in 0-1 is not a loss'; END IF;
    IF chess.outcome(false, r_black) <> 2 THEN RAISE EXCEPTION 'FAIL: black in 0-1 is not a win'; END IF;
    IF chess.outcome(true,  r_draw)  <> 1 THEN RAISE EXCEPTION 'FAIL: white in 1/2-1/2 is not a draw'; END IF;
    IF chess.outcome(false, r_draw)  <> 1 THEN RAISE EXCEPTION 'FAIL: black in 1/2-1/2 is not a draw'; END IF;
    -- an unreadable result token ABSTAINS; it must never be scored as a draw
    IF chess.outcome(true, r_junk) IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: an unrecognised result token was scored instead of abstaining';
    END IF;
    IF chess.outcome(true, NULL) IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: a missing result was scored instead of abstaining';
    END IF;

    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL),
        (tal, 0, type_t, src), (botv, 0, type_t, src), (spas, 0, type_t, src),
        (e1, 0, type_t, src), (e2, 0, type_t, src),
        (e3, 0, type_t, src), (e4, 0, type_t, src),
        (ln_a, 0, type_t, src), (ln_b, 0, type_t, src), (ln_c, 0, type_t, src),
        (pos2, 0, type_t, src), (mv_w, 0, type_t, src), (mv_b, 0, type_t, src),
        (r_white, 0, type_t, src), (r_black, 0, type_t, src), (r_draw, 0, type_t, src),
        (d1, 0, type_t, src), (d2, 0, type_t, src), (d3, 0, type_t, src),
        (en, 0, type_t, src), (ec, 0, type_t, src), (mt, 0, type_t, src),
        (elo, 0, type_t, src), (pos_probe, 0, type_t, src);

    -- e1 Tal(W) beat Botvinnik on ln_a    e2 Botvinnik(W) beat Tal on ln_b
    -- e3 Tal(W) drew Spassky on ln_c      e4 Tal(W) v Botvinnik, never scored,
    -- and e4 REPLAYS ln_a: the same content played twice. (event, PLAYS_LINE,
    -- line) is the one event-subject record edge the game reads hop, carrying
    -- the playing's white-POV score; every header subjects on the LINE with
    -- ctx = its own playing, so e1's result must never cross onto e4's
    -- appearance.
    INSERT INTO attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count,
         sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES
        (laplace_hash128_blake3('t2/e1pl'), e1, plns, ln_a, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e2pl'), e2, plns, ln_b, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e3pl'), e3, plns, ln_c, src, NULL, 1, now(), 1, 500000000, 30000000000),
        (laplace_hash128_blake3('t2/e4pl'), e4, plns, ln_a, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1w'), ln_a, hw, tal,  src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1b'), ln_a, hb, botv, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1r'), ln_a, hr, r_white, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1d'), ln_a, od, d1, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1e'), ln_a, hev, en, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1c'), ln_a, heco, ec, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1m'), ln_a, hmt, mt, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e2w'), ln_b, hw, botv, src, e2, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e2b'), ln_b, hb, tal,  src, e2, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e2r'), ln_b, hr, r_white, src, e2, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e2d'), ln_b, od, d2, src, e2, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e3w'), ln_c, hw, tal,  src, e3, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e3b'), ln_c, hb, spas, src, e3, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e3r'), ln_c, hr, r_draw, src, e3, 1, now(), 1, 500000000, 30000000000),
        (laplace_hash128_blake3('t2/e3d'), ln_c, od, d3, src, e3, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e4w'), ln_a, hw, tal,  src, e4, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e4b'), ln_a, hb, botv, src, e4, 2, now(), 1, 1000000000, 30000000000),
        -- the shared opening position: Tal's White choice in e1, and the move
        -- played in e2 while he sat Black -- MOVE ctx = the playing-EVENT
        (laplace_hash128_blake3('t2/mv_e1'), pos2, mv, mv_w, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/mv_e2'), pos2, mv, mv_b, src, e2, 0, now(), 1, 0, 30000000000),
        -- the players' own aggregating lane (AppendPlayerResult), ctx = the playing
        (laplace_hash128_blake3('t2/e1ot'), tal,  outc, cres, src, e1, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e1ob'), botv, outc, cres, src, e1, 0, now(), 1, 0, 30000000000),
        (laplace_hash128_blake3('t2/e2ob'), botv, outc, cres, src, e2, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/e2ot'), tal,  outc, cres, src, e2, 0, now(), 1, 0, 30000000000),
        (laplace_hash128_blake3('t2/e3ot'), tal,  outc, cres, src, e3, 1, now(), 1, 500000000, 30000000000),
        (laplace_hash128_blake3('t2/e3os'), spas, outc, cres, src, e3, 1, now(), 1, 500000000, 30000000000),
        -- an Elo tag whose surface cannot be rendered back to digits
        (laplace_hash128_blake3('t2/rat'), tal, hrat, elo, src, e1, 2, now(), 1, 1000000000, 30000000000);

    -- chess_player_record: the career total. 4 playings — one won, one lost, one
    -- drawn, one the source never scored. score is over SCORED games only:
    -- (1 win + 1/2 draw) / 3 = 0.5. Counting the unscored game as a loss would
    -- give 0.375, and silently dropping it from `games` would hide it entirely.
    -- e4 shares ln_a with e1, so this only holds if the result join threads the
    -- CONTEXT: on the line alone, e1's 1-0 would score e4 too.
    SELECT games, wins, draws, losses, unscored, score
      INTO n, w, d, l, u, sc
      FROM chess.player_record(tal) WHERE as_white IS NULL;
    IF n <> 4 OR w <> 1 OR d <> 1 OR l <> 1 OR u <> 1 THEN
        RAISE EXCEPTION 'FAIL: Tal career expected 4/1w/1d/1l/1u, got %/%/%/%/%', n, w, d, l, u;
    END IF;
    IF sc IS DISTINCT FROM 0.5 THEN
        RAISE EXCEPTION 'FAIL: score must be over scored games only, got %', sc;
    END IF;

    -- the colour splits come from the same pass, so they must reconcile exactly
    SELECT sum(games), sum(wins), sum(draws), sum(losses), sum(unscored)
      INTO n, w, d, l, u
      FROM chess.player_record(tal) WHERE as_white IS NOT NULL;
    IF n <> 4 OR w <> 1 OR d <> 1 OR l <> 1 OR u <> 1 THEN
        RAISE EXCEPTION 'FAIL: colour splits do not reconcile with the total: %/%/%/%/%', n, w, d, l, u;
    END IF;
    SELECT games INTO n FROM chess.player_record(tal) WHERE as_white;
    IF n <> 3 THEN RAISE EXCEPTION 'FAIL: Tal as White expected 3 games, got %', n; END IF;
    SELECT games, losses INTO n, l FROM chess.player_record(tal) WHERE NOT as_white;
    IF n <> 1 OR l <> 1 THEN
        RAISE EXCEPTION 'FAIL: Tal as Black expected 1 game / 1 loss, got %/%', n, l;
    END IF;

    -- a player nobody has witnessed has an empty record, not an error
    SELECT games INTO n FROM chess.player_record(chess.player_id('Nobody At All'))
     WHERE as_white IS NULL;
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: unwitnessed player reported % games', n; END IF;

    -- chess_player_games: the log behind the record, keyed by the playing-EVENT
    -- id and ordered by each playing's own asserted date, most recent first —
    -- the undated e4 sorts last rather than being dropped. The Black game is the
    -- loss, and it names the opponent from the far side of the same playing.
    SELECT count(*) INTO n FROM chess.player_games(tal, 25, 0);
    IF n <> 4 THEN RAISE EXCEPTION 'FAIL: Tal game log expected 4 rows, got %', n; END IF;
    SELECT g.event_id INTO got FROM chess.player_games(tal, 25, 0) g LIMIT 1;
    IF got <> e3 THEN RAISE EXCEPTION 'FAIL: the log should lead with the latest dated playing (e3)'; END IF;
    SELECT as_white, outcome, opponent_id INTO b, l, got
      FROM chess.player_games(tal, 25, 0) WHERE event_id = e2;
    IF b OR l <> 0 OR got <> botv THEN
        RAISE EXCEPTION 'FAIL: e2 should be Tal as Black, lost, to Botvinnik — got %/%/%', b, l, got;
    END IF;
    -- the unscored playing is LISTED, with an abstaining outcome — even though
    -- its shared line carries e1's result under e1's context
    SELECT outcome INTO l FROM chess.player_games(tal, 25, 0) WHERE event_id = e4;
    IF l IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: the unscored playing was given outcome % instead of abstaining', l;
    END IF;
    -- the transcribed headers ride the same line+context join
    SELECT g.eco INTO txt FROM chess.player_games(tal, 25, 0) g WHERE g.event_id = e1;
    IF txt IS DISTINCT FROM 'C' THEN
        RAISE EXCEPTION 'FAIL: e1 ECO should realize to C, got %', txt;
    END IF;
    -- with no renderable name, the opponent falls back to its id rather than blank
    SELECT opponent INTO txt FROM chess.player_games(tal, 25, 0) WHERE event_id = e1;
    IF txt IS NULL OR txt = '' THEN
        RAISE EXCEPTION 'FAIL: nameless opponent rendered empty instead of falling back to hex';
    END IF;
    -- paging
    SELECT count(*) INTO n FROM chess.player_games(tal, 2, 0);
    IF n <> 2 THEN RAISE EXCEPTION 'FAIL: chess_player_games LIMIT 2 got % rows', n; END IF;
    SELECT count(*) INTO n FROM chess.player_games(tal, 25, 4);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: paging past the end got % rows', n; END IF;

    -- chess_game: the playing-EVENT handle hops (event, PLAYS_LINE, line) to the
    -- content, then reads the headers pinned to THIS playing's context — both
    -- sides followable back to their careers, and the record movetext rendered
    SELECT white_id INTO got FROM chess.game(e1);
    IF got <> tal THEN RAISE EXCEPTION 'FAIL: e1 White should be Tal'; END IF;
    SELECT black_id INTO got FROM chess.game(e1);
    IF got <> botv THEN RAISE EXCEPTION 'FAIL: e1 Black should be Botvinnik'; END IF;
    SELECT played_on INTO txt FROM chess.game(e1);
    IF txt IS DISTINCT FROM '1' THEN RAISE EXCEPTION 'FAIL: e1 date should realize to 1, got %', txt; END IF;
    SELECT movetext INTO txt FROM chess.game(e1);
    IF txt IS DISTINCT FROM 'm' THEN RAISE EXCEPTION 'FAIL: e1 movetext should render to m, got %', txt; END IF;
    SELECT count(*) INTO n FROM chess.game(laplace_hash128_blake3('test/chess2/no_such_game'));
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: an unwitnessed playing returned % rows', n; END IF;

    -- chess_player_moves across the re-key: MOVE evidence carries ctx = the
    -- playing-EVENT and the colour facts subject the LINE, so the repertoire
    -- join threads event equality. Same shared position, per-colour
    -- attribution: as White Tal chose mv_w (e1, won); e2's move must surface
    -- only on his Black side, never leak through the shared line subjects.
    SELECT count(*) INTO n FROM chess.player_moves(pos2, tal, true);
    IF n <> 1 THEN RAISE EXCEPTION 'FAIL: Tal-as-White repertoire expected 1 move, got %', n; END IF;
    SELECT next_position, games, score INTO got, n, sc FROM chess.player_moves(pos2, tal, true);
    IF got <> mv_w OR n <> 1 OR sc <> 1.0 THEN
        RAISE EXCEPTION 'FAIL: Tal as White should show mv_w won once, got %/%/%', got, n, sc;
    END IF;
    SELECT next_position, score INTO got, sc FROM chess.player_moves(pos2, tal, false);
    IF got <> mv_b OR sc <> 0.0 THEN
        RAISE EXCEPTION 'FAIL: Tal as Black should see the e2 move as a loss, got %/%', got, sc;
    END IF;

    -- chess_player_ratings: an Elo tag that will not render back to digits is an
    -- unreadable witness and is SKIPPED, never coerced to a number.
    SELECT count(*) INTO n FROM chess.player_ratings(tal);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: an unparseable rating tag was coerced, got % rows', n; END IF;

    -- chess_ranked / chess_head_to_head: the FOLDED reads. Same three players, but rated
    -- through the aggregating lane instead of counted by a GROUP BY. Tal is given a strong
    -- cell, Spassky a thin one, so the conservative estimate has to order them by strength
    -- rather than by games -- the thing a win percentage cannot express.
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (laplace_hash128_blake3('t2/outcome_obj'), 0, type_t, src)
    ON CONFLICT DO NOTHING;
    UPDATE entities SET type_id = entity_type_id('Chess_Player')
     WHERE id IN (tal, botv, spas);

    INSERT INTO consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
    VALUES
        -- eff_mu = rating - 2*rd: tal 1800e9, botv 1500e9, spas 1100e9 (thin, wide RD)
        (laplace_hash128_blake3('t2/c_tal'),  tal,  relation_type_id('OUTCOME'),
             entity_type_id('Chess_Result'), 1900000000000, 50000000000, 60000000, 40, now()),
        (laplace_hash128_blake3('t2/c_botv'), botv, relation_type_id('OUTCOME'),
             entity_type_id('Chess_Result'), 1600000000000, 50000000000, 60000000, 30, now()),
        (laplace_hash128_blake3('t2/c_spas'), spas, relation_type_id('OUTCOME'),
             entity_type_id('Chess_Result'), 1500000000000, 200000000000, 60000000, 1, now()),
        (laplace_hash128_blake3('t2/h2h'),    tal,  relation_type_id('PLAYED_BY'),
             botv, 1700000000000, 60000000000, 60000000, 28, now()),
        -- the LINE's own fold cell (GH #736): witness_count IS times played (e1 + e4)
        (laplace_hash128_blake3('t2/c_line_a'), ln_a, relation_type_id('OUTCOME'),
             entity_type_id('Chess_Result'), 1700000000000, 50000000000, 60000000, 2, now());

    SELECT player_id INTO got FROM chess.ranked(10) WHERE rank = 1;
    IF got <> tal THEN RAISE EXCEPTION 'FAIL: chess_ranked rank 1 should be Tal (highest eff_mu)'; END IF;

    -- witness_count IS games played: the fold carries the count, nothing recomputes it.
    SELECT games INTO n FROM chess.ranked(10) WHERE player_id = tal;
    IF n <> 40 THEN RAISE EXCEPTION 'FAIL: chess_ranked games should be witness_count (40), got %', n; END IF;

    -- The thin one-game record sinks on RD rather than flattering itself -- no floor needed,
    -- and no floor used: he is still on the board, just last. The line's identically shaped
    -- (OUTCOME, Chess_Result) cell stays out: it is not a Chess_Player subject.
    SELECT count(*) INTO n FROM chess.ranked(10);
    IF n <> 3 THEN RAISE EXCEPTION 'FAIL: chess_ranked expected 3 players, got %', n; END IF;
    SELECT rank INTO n FROM chess.ranked(10) WHERE player_id = spas;
    IF n <> 3 THEN RAISE EXCEPTION 'FAIL: the thin record should rank last on RD, got rank %', n; END IF;

    -- Positions carry the IDENTICAL (OUTCOME, Chess_Result) cell shape; the subject's entity
    -- type is what separates them, so a position must never appear in a player ranking.
    INSERT INTO consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
    VALUES (laplace_hash128_blake3('t2/c_pos'), pos_probe, relation_type_id('OUTCOME'),
            entity_type_id('Chess_Result'), 9900000000000, 1000000000, 60000000, 999, now());
    SELECT count(*) INTO n FROM chess.ranked(10) WHERE player_id = pos_probe;
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: a position leaked into the player ranking'; END IF;

    -- Paging is over the ranking, not a re-aggregate.
    SELECT player_id INTO got FROM chess.ranked(1, 1);
    IF got <> botv THEN RAISE EXCEPTION 'FAIL: chess_ranked OFFSET 1 should be Botvinnik'; END IF;

    -- rating/rd/eff_mu leave chess_ranked in DISPLAY units (the eff_mu_display /1e9
    -- round), never raw fp1e9: Tal's cell is 1900.0 / 50.0 / 1800.0, not 1.9e12.
    SELECT r.rating, r.rd, r.eff_mu INTO ra, rdv, mu FROM chess.ranked(10) r WHERE r.player_id = tal;
    IF ra <> 1900.0 OR rdv <> 50.0 OR mu <> 1800.0 THEN
        RAISE EXCEPTION 'FAIL: chess_ranked must return display-scale rating/rd/eff_mu, got %/%/%', ra, rdv, mu;
    END IF;

    -- Head to head is ONE folded cell per pairing: 28 meetings, 28 witnesses, no regroup.
    SELECT games INTO n FROM chess.head_to_head(tal, 10) WHERE opponent_id = botv;
    IF n <> 28 THEN RAISE EXCEPTION 'FAIL: head-to-head should carry 28 witnesses, got %', n; END IF;
    SELECT count(*) INTO n FROM chess.head_to_head(spas, 10);
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: unplayed pairing returned % rows', n; END IF;

    -- ... and its rating columns are display-scale too: 1700.0 / 60.0 / 1580.0.
    SELECT h.rating, h.rd, h.eff_mu INTO ra, rdv, mu
      FROM chess.head_to_head(tal, 10) h WHERE h.opponent_id = botv;
    IF ra <> 1700.0 OR rdv <> 60.0 OR mu <> 1580.0 THEN
        RAISE EXCEPTION 'FAIL: chess_head_to_head must return display-scale rating/rd/eff_mu, got %/%/%', ra, rdv, mu;
    END IF;

    -- chess_line: the line-grain read the re-key pays for. ln_a was played
    -- twice — e1 scored and dated, e4 neither — so it returns one row per
    -- playing, each pinned to its OWN headers by context, while the line's
    -- single fold cell repeats on both: witness_count IS times played.
    SELECT count(*) INTO n FROM chess.line(ln_a);
    IF n <> 2 THEN RAISE EXCEPTION 'FAIL: chess_line expected 2 playings of the shared line, got %', n; END IF;
    SELECT cl.event_id INTO got FROM chess.line(ln_a) cl LIMIT 1;
    IF got <> e1 THEN RAISE EXCEPTION 'FAIL: chess_line should lead with the dated playing (e1)'; END IF;
    SELECT cl.played_on, cl.times_played INTO txt, n FROM chess.line(ln_a) cl WHERE cl.event_id = e4;
    IF txt IS NOT NULL OR n <> 2 THEN
        RAISE EXCEPTION 'FAIL: e4 should be undated yet carry the shared fold (2 plays), got %/%', txt, n;
    END IF;
    SELECT count(*) INTO n FROM chess.line(laplace_hash128_blake3('test/chess2/no_such_line'));
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: a line nobody played returned % rows', n; END IF;

    -- chess_players_by_initial: Tal browsable under 'T' via a name whose trajectory's
    -- first constituent IS the codepoint, bound to him by a HAS_NAME_ALIAS cell -- and
    -- the same display-scale law on the rating columns.
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (cap_t, 0, type_t, src), (tname, 0, type_t, src);
    INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (laplace_hash128_blake3('t2/name/tal_phys'), tname, 1,
            'SRID=0;POINT ZM (0 0 0 0)'::geometry, decode(repeat('00', 16), 'hex'),
            laplace_trajectory_build(ARRAY[cap_t]), 1, now());
    INSERT INTO consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
    VALUES (laplace_hash128_blake3('t2/c_alias'), tal, relation_type_id('HAS_NAME_ALIAS'),
            tname, 1500000000000, 50000000000, 60000000, 1, now());

    SELECT p.rating, p.rd, p.eff_mu INTO ra, rdv, mu
      FROM chess.players_by_initial('T', 10, 0) p WHERE p.player_id = tal;
    IF ra IS NULL THEN
        RAISE EXCEPTION 'FAIL: chess_players_by_initial did not surface Tal under T';
    END IF;
    IF ra <> 1900.0 OR rdv <> 50.0 OR mu <> 1800.0 THEN
        RAISE EXCEPTION 'FAIL: chess_players_by_initial must return display-scale rating/rd/eff_mu, got %/%/%', ra, rdv, mu;
    END IF;

    -- chess_game_plies: a playing read back as LOOKUPS. The trajectory lives on
    -- the LINE (built with the SAME native packer the compose path uses, so this
    -- fixture is byte-identical to a real deposit), the playing-EVENT hops to it
    -- through PLAYS_LINE, and per-ply facts join by context: the EVENT for this
    -- playing's own testimony, the LINE for line-grain engine testimony.
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (pl_p0, 0, type_t, src), (pl_p1, 0, type_t, src), (pl_p2, 0, type_t, src),
        (pl_line, 0, type_t, src), (pl_event, 0, type_t, src),
        (pl_sa, 0, type_t, src), (pl_sb, 0, type_t, src),
        (pl_sc, 0, type_t, src), (qg, 0, type_t, src);
    INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index,
                               trajectory, n_constituents, observed_at)
    VALUES (laplace_hash128_blake3('t2/plies/phys'), pl_line, 1,
            'SRID=0;POINT ZM (0 0 0 0)'::geometry, decode(repeat('00', 16), 'hex'),
            laplace_trajectory_build(ARRAY[pl_p0, pl_p1, pl_p2]), 3, now());

    -- the packer/decoder round-trip: the ids come back, in order
    SELECT array_agg(entity_id ORDER BY ordinal) INTO ids_got
    FROM laplace_trajectory_constituents(
             laplace_trajectory_build(ARRAY[pl_p0, pl_p1, pl_p2]));
    IF ids_got <> ARRAY[pl_p0, pl_p1, pl_p2] THEN
        RAISE EXCEPTION 'FAIL: trajectory build/decode round-trip lost the sequence';
    END IF;

    INSERT INTO attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count,
         sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES
        (laplace_hash128_blake3('t2/plies/pl'), pl_event, plns, pl_line, src, NULL, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/plies/san0'), pl_p0, relation_type_id('HAS_SAN'),
             pl_sa, src, pl_event, 2, now(), 1, 1000000000, 30000000000),
        (laplace_hash128_blake3('t2/plies/san1'), pl_p1, relation_type_id('HAS_SAN'),
             pl_sb, src, pl_event, 2, now(), 1, 1000000000, 30000000000),
        -- line-grain engine testimony rides ctx = the LINE and must be included
        (laplace_hash128_blake3('t2/plies/qual'), pl_p1, mq,
             qg, src, pl_line, 2, now(), 1, 1000000000, 30000000000),
        -- another playing's ply on the SAME shared position: context must exclude it
        (laplace_hash128_blake3('t2/plies/other'), pl_p0, relation_type_id('HAS_SAN'),
             pl_sc, src, e1, 2, now(), 1, 1000000000, 30000000000);

    SELECT count(*) INTO n FROM chess.game_plies(pl_event);
    IF n <> 3 THEN
        RAISE EXCEPTION 'FAIL: chess_game_plies expected 3 vertices, got %', n;
    END IF;

    -- vertex 1 is the starting position; the SAN on a vertex is the move that LEFT it,
    -- and the OTHER playing's SAN on the same shared position must not appear
    SELECT position_id INTO got FROM chess.game_plies(pl_event) WHERE ply = 1;
    IF got <> pl_p0 THEN RAISE EXCEPTION 'FAIL: vertex 1 is not the start position'; END IF;
    SELECT count(*) INTO n FROM chess.game_plies(pl_event) WHERE san IS NOT NULL;
    IF n <> 2 THEN
        RAISE EXCEPTION 'FAIL: expected 2 SANs scoped to this playing, got % (context leak?)', n;
    END IF;
    -- ... and the line-grain quality joins through the LINE context branch
    SELECT count(*) INTO n FROM chess.game_plies(pl_event) WHERE move_quality IS NOT NULL;
    IF n <> 1 THEN
        RAISE EXCEPTION 'FAIL: line-grain move quality expected on exactly 1 ply, got %', n;
    END IF;

    -- a playing whose line has no trajectory returns nothing rather than guessing
    -- an order, and so does an event handle with no PLAYS_LINE record at all
    SELECT count(*) INTO n FROM chess.game_plies(e1);
    IF n <> 0 THEN
        RAISE EXCEPTION 'FAIL: a line with no trajectory returned % rows instead of abstaining', n;
    END IF;
    SELECT count(*) INTO n FROM chess.game_plies(laplace_hash128_blake3('test/chess2/no_such_game'));
    IF n <> 0 THEN
        RAISE EXCEPTION 'FAIL: an unrecorded playing returned % rows', n;
    END IF;

    RAISE NOTICE '✓ chess_read: name folding is content-addressed, W/L/D abstains on unscored playings, career and log reconcile through the line/event re-key, folded reads rank by eff_mu';
END $$;

-- chess_time_pressure_outcome: the folded think-class read, now carrying the lens
-- dimension (planned_quick / pressed_think / flagging beside rushed / normal / deep).
-- Post-#736 shape: HAS_THINK_CLASS and MOVE cells both subject the POSITION; the
-- playing's event enters only as evidence context, which this folded read never touches.
DO $$
DECLARE
    type_t   bytea := laplace_hash128_blake3('Type');
    mv       bytea := relation_type_id('MOVE');
    tcl      bytea := relation_type_id('HAS_THINK_CLASS');
    src      bytea := laplace_hash128_blake3('test/chess3/source');
    p_rush   bytea := laplace_hash128_blake3('test/chess3/p_rush');
    p_plan   bytea := laplace_hash128_blake3('test/chess3/p_plan');
    p_press  bytea := laplace_hash128_blake3('test/chess3/p_press');
    p_flag   bytea := laplace_hash128_blake3('test/chess3/p_flag');
    nx       bytea := laplace_hash128_blake3('test/chess3/next');
    w_rush   bytea := word_id('rushed');
    w_plan   bytea := word_id('planned_quick');
    w_press  bytea := word_id('pressed_think');
    w_flag   bytea := word_id('flagging');
    labels   text[];
    n        bigint;
    mu       numeric;
BEGIN
    INSERT INTO entities (id, tier, type_id, first_observed_by) VALUES
        (src, 0, type_t, NULL), (nx, 0, type_t, src),
        (p_rush, 0, type_t, src), (p_plan, 0, type_t, src),
        (p_press, 0, type_t, src), (p_flag, 0, type_t, src),
        (w_rush, 0, type_t, src), (w_plan, 0, type_t, src),
        (w_press, 0, type_t, src), (w_flag, 0, type_t, src)
    ON CONFLICT DO NOTHING;

    -- One MOVE cell per position (the folded-outcome side of the join) and one
    -- think-class cell naming its class. eff_mu(rushed) = 1600e9 - 2*50e9 = 1500e9,
    -- which must read back display-scale (1500.000), never raw fp1e9.
    INSERT INTO consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility, witness_count, last_observed_at)
    VALUES
        (laplace_hash128_blake3('t3/mv_rush'),  p_rush,  mv, nx, 1600000000000, 50000000000, 60000000, 10, now()),
        (laplace_hash128_blake3('t3/mv_plan'),  p_plan,  mv, nx, 1500000000000, 50000000000, 60000000, 20, now()),
        (laplace_hash128_blake3('t3/mv_press'), p_press, mv, nx, 1400000000000, 50000000000, 60000000, 5, now()),
        (laplace_hash128_blake3('t3/mv_flag'),  p_flag,  mv, nx, 1200000000000, 50000000000, 60000000, 2, now()),
        (laplace_hash128_blake3('t3/tc_rush'),  p_rush,  tcl, w_rush,  1500000000000, 50000000000, 60000000, 3, now()),
        (laplace_hash128_blake3('t3/tc_plan'),  p_plan,  tcl, w_plan,  1500000000000, 50000000000, 60000000, 4, now()),
        (laplace_hash128_blake3('t3/tc_press'), p_press, tcl, w_press, 1500000000000, 50000000000, 60000000, 2, now()),
        (laplace_hash128_blake3('t3/tc_flag'),  p_flag,  tcl, w_flag,  1500000000000, 50000000000, 60000000, 1, now());

    -- Exactly the witnessed classes come back, in lens-ladder order; classes with no
    -- witnessed cell (normal/deep here) produce no row rather than a zero row.
    SELECT array_agg(q.think_class) INTO labels FROM chess.time_pressure_outcome() q;
    IF labels <> ARRAY['rushed', 'planned_quick', 'pressed_think', 'flagging'] THEN
        RAISE EXCEPTION 'FAIL: lens dimension wrong or misordered: %', labels;
    END IF;

    -- plays is the MOVE fold's witness_count, carried not recomputed.
    SELECT q.plays INTO n FROM chess.time_pressure_outcome() q WHERE q.think_class = 'planned_quick';
    IF n <> 20 THEN RAISE EXCEPTION 'FAIL: planned_quick plays should be 20, got %', n; END IF;

    -- avg_eff_mu leaves in DISPLAY units (the eff_mu_display /1e9 round).
    SELECT q.avg_eff_mu INTO mu FROM chess.time_pressure_outcome() q WHERE q.think_class = 'rushed';
    IF mu <> 1500.000 THEN
        RAISE EXCEPTION 'FAIL: avg_eff_mu must be display-scale, got %', mu;
    END IF;

    RAISE NOTICE '✓ chess_read: chess_time_pressure_outcome serves the think-time lens dimension in display units';
END $$;

ROLLBACK;
