CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

BEGIN;

-- A Chess_Player can have more than one consensus cell using the outcome relation.
-- Only the canonical result object is the player's standing. A different object must
-- never duplicate the player or inject its carrier into the leaderboard/search result.
DO $$
DECLARE
    player bytea := chess.player_id('rank-scope-probe');
    rogue  bytea := decode(repeat('e2', 16), 'hex');
    src    bytea := laplace.source_id('ChessPgn');
    n bigint;
    g bigint;
    r double precision;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
        (player, 0, laplace.entity_type_id('Chess_Player'), src),
        (rogue,  0, laplace.entity_type_id('Chess_Result'), src)
    ON CONFLICT DO NOTHING;

    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility,
         witness_count, last_observed_at)
    VALUES
        (decode(repeat('e3', 16), 'hex'), player, laplace.relation_type_id('OUTCOME'),
         laplace.entity_type_id('Chess_Result'),
         1900000000000, 50000000000, 60000000, 40, now()),
        (decode(repeat('e4', 16), 'hex'), player, laplace.relation_type_id('OUTCOME'),
         rogue,
         9223372036854775807, 350000000000, 60000000, 999, now());

    SELECT count(*), max(games), max(rating)
      INTO n, g, r
      FROM chess.ranked(10)
     WHERE player_id = player;

    IF n <> 1 THEN
        RAISE EXCEPTION 'leaderboard duplicated one player across outcome objects: % rows', n;
    END IF;
    IF g <> 40 THEN
        RAISE EXCEPTION 'leaderboard read witness count from the wrong outcome object: %', g;
    END IF;
    IF r <> 1900.0 THEN
        RAISE EXCEPTION 'leaderboard read rating from the wrong outcome object: %', r;
    END IF;

    SELECT count(*), max(games), max(rating)
      INTO n, g, r
      FROM chess.player_search_candidates(ARRAY['rank-scope-probe'], 10)
     WHERE player_id = player;

    IF n <> 1 THEN
        RAISE EXCEPTION 'exact player search duplicated one player across outcome objects: % rows', n;
    END IF;
    IF g <> 40 THEN
        RAISE EXCEPTION 'exact player search read witness count from the wrong outcome object: %', g;
    END IF;
    IF r <> 1900.0 THEN
        RAISE EXCEPTION 'exact player search read rating from the wrong outcome object: %', r;
    END IF;
END $$;

-- Rankings compare complete fixed-point values before display rounding. Above
-- 2^53 adjacent int64 values collapse to one double, so reversing the id tie
-- direction makes a premature conversion observable on either page order.
DO $$
DECLARE
    low_id bytea := decode(repeat('01',16),'hex');
    high_id bytea := decode(repeat('fe',16),'hex');
    got bytea;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id) VALUES
        (low_id,0,laplace.entity_type_id('Chess_Player')),
        (high_id,0,laplace.entity_type_id('Chess_Player'));
    INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES
        (decode(repeat('d1',16),'hex'),low_id,laplace.relation_type_id('OUTCOME'),laplace.entity_type_id('Chess_Result'),9007199254740992,1,60000000,1,now()),
        (decode(repeat('d2',16),'hex'),high_id,laplace.relation_type_id('OUTCOME'),laplace.entity_type_id('Chess_Result'),9007199254740993,1,60000000,1,now());
    SELECT player_id INTO got FROM chess.ranked(1,0,'rating','desc');
    IF got <> high_id THEN RAISE EXCEPTION 'fixed-point rank lost precision before display'; END IF;
    SELECT player_id INTO got FROM chess.ranked(1,1,'rating','desc');
    IF got <> low_id THEN RAISE EXCEPTION 'second page did not retain exact standing order'; END IF;
END $$;

-- Real compositional name floors: a surname is an operand of each full name,
-- while its own letters belong to a lower trajectory. Selection must ascend
-- that boundary and preserve letter order before ranking the complete set.
DO $$
DECLARE
    a bytea := chess.player_id('Carlsen, Magnus');
    b bytea := chess.player_id('Carlsen, Inga');
    surname bytea := laplace.word_id('Carlsen');
    aname bytea := laplace.word_id('Carlsen, Magnus');
    bname bytea := laplace.word_id('Carlsen, Inga');
    got bytea;
    n bigint;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id) VALUES
        (a,0,laplace.entity_type_id('Chess_Player')),
        (b,0,laplace.entity_type_id('Chess_Player')),
        (surname,2,laplace.entity_type_id('Word')),
        (aname,3,laplace.entity_type_id('Sentence')),
        (bname,3,laplace.entity_type_id('Sentence'));
    INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
    SELECT public.laplace_hash128_blake3(e.id || decode('0100','hex')), e.id,1,
        'SRID=0;POINT ZM(0 0 0 0)'::geometry,decode(repeat('00',16),'hex'),
        public.laplace_trajectory_build(e.members),cardinality(e.members),now()
    FROM (VALUES
        (surname,ARRAY[laplace.word_id('C'),laplace.word_id('a'),laplace.word_id('r'),laplace.word_id('l'),laplace.word_id('s'),laplace.word_id('e'),laplace.word_id('n')]),
        (aname,ARRAY[surname,laplace.word_id(','),laplace.word_id(' '),laplace.word_id('Magnus')]),
        (bname,ARRAY[surname,laplace.word_id(','),laplace.word_id(' '),laplace.word_id('Inga')])
    ) e(id,members);
    INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    SELECT laplace.consensus_id(v.player,v.kind,v.object),v.player,v.kind,v.object,
        1500000000000,350000000000,60000000,v.witnesses,now()
    FROM (VALUES
        (a,laplace.relation_type_id('HAS_NAME'),aname,1),
        (b,laplace.relation_type_id('HAS_NAME'),bname,1),
        (a,laplace.relation_type_id('OUTCOME'),laplace.entity_type_id('Chess_Result'),80)
    ) v(player,kind,object,witnesses);
    SELECT count(*) INTO n FROM chess.player_search_candidates(ARRAY['Carlsen'],100);
    IF n<>2 THEN RAISE EXCEPTION 'surname did not ascend into both witnessed names: %',n; END IF;
    SELECT player_id INTO got FROM chess.player_search_candidates(ARRAY['Carlsen'],1,0,'games','asc');
    IF got IS DISTINCT FROM b THEN RAISE EXCEPTION 'first ascending games page lost profile-only name'; END IF;
    SELECT player_id INTO got FROM chess.player_search_candidates(ARRAY['Carlsen'],1,1,'games','asc');
    IF got IS DISTINCT FROM a THEN RAISE EXCEPTION 'second page lost witnessed player'; END IF;
    SELECT count(*) INTO n FROM chess.player_search_candidates(ARRAY['Carlsen'],1,2,'games','asc');
    IF n<>0 THEN RAISE EXCEPTION 'past-end page was not empty'; END IF;
    SELECT count(*) INTO n FROM chess.player_search_candidates(ARRAY['Carls'],100);
    IF n<>2 THEN RAISE EXCEPTION 'ordered word fragment did not ascend through surname: %',n; END IF;
    SELECT count(*) INTO n FROM chess.player_search_candidates(ARRAY['Clasren'],100);
    IF n<>0 THEN RAISE EXCEPTION 'anagram passed ordered containment'; END IF;
    SELECT count(*) INTO n FROM chess.player_search_candidates(ARRAY['Carlsen'],100,0,'games','asc',true);
    IF n<>0 THEN RAISE EXCEPTION 'exact-only lookup expanded the surname'; END IF;
    SELECT count(*) INTO n FROM chess.player_search_candidates(ARRAY['Carlsen, Magnus'],100);
    IF n<>1 THEN RAISE EXCEPTION 'exact lookup did not terminate'; END IF;
END $$;

SELECT 'chess ranked canonical result scope' AS probe, true AS ok;

ROLLBACK;
