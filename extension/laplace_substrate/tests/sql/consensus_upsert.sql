BEGIN;

DO $$
DECLARE
    type_t   bytea := public.laplace_hash128_blake3('Type');
    src      bytea := public.laplace_hash128_blake3('test/upsert/source');
    rel_a    bytea := public.laplace_hash128_blake3('test/upsert/rel_a');
    rel_hot  bytea := laplace.relation_type_id('IS_A');
    subj     bytea := public.laplace_hash128_blake3('test/upsert/subject');
    o1       bytea := public.laplace_hash128_blake3('test/upsert/obj1');
    o2       bytea := public.laplace_hash128_blake3('test/upsert/obj2');
    phi      bigint := 30000000000;
    s_conf   bigint := 900000000;
    s_ref    bigint := 100000000;
    neutral  bigint := 1500000000000;
    t1       timestamptz := '2026-01-01 00:00:00+00';
    t2       timestamptz := '2026-02-01 00:00:00+00';
    affected bigint;
    row1     laplace.consensus%ROWTYPE;
    row2     laplace.consensus%ROWTYPE;
    expect   laplace.laplace_glicko2_result;
    leafname text;
    dup_ok   boolean := false;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id) VALUES
        (src, 0, type_t), (rel_a, 0, type_t), (subj, 0, type_t),
        (o1, 0, type_t), (o2, 0, type_t)
    ON CONFLICT (id) DO NOTHING;

    -- 1) fresh insert: three cells in one ordered call (hot relation, plain
    --    relation, NULL object). Every row folds from the initial priors via
    --    the same native scalar the upsert uses.
    affected := consensus.upsert(
        ARRAY[subj, subj, subj], ARRAY[rel_hot, rel_a, rel_a],
        ARRAY[o1, o2, NULL],
        ARRAY[phi, phi, phi], ARRAY[2, 1, 3]::bigint[],
        ARRAY[2 * s_conf, s_ref, 3 * s_conf]::bigint[],
        ARRAY[t1, t1, t1]);
    IF affected <> 3 THEN
        RAISE EXCEPTION 'FAIL: fresh upsert affected % rows, expected 3', affected;
    END IF;

    SELECT * INTO row1 FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_hot AND object_id = o1;
    IF NOT FOUND THEN RAISE EXCEPTION 'FAIL: hot-relation cell missing'; END IF;
    IF row1.witness_count <> 2 THEN
        RAISE EXCEPTION 'FAIL: fresh witness_count=%, expected 2', row1.witness_count;
    END IF;
    IF row1.rating <= neutral THEN
        RAISE EXCEPTION 'FAIL: confirming evidence must lift rating above neutral';
    END IF;
    expect := laplace.laplace_glicko2_accumulate_period(
        consensus.glicko2_neutral_mu(), consensus.glicko2_initial_rd(), consensus.glicko2_initial_volatility(),
        ARRAY[consensus.glicko2_neutral_mu()]::bigint[], ARRAY[phi]::bigint[],
        ARRAY[2]::bigint[], ARRAY[2 * s_conf]::bigint[], consensus.glicko2_tau());
    IF row1.rating <> expect.rating OR row1.rd <> expect.rd
       OR row1.volatility <> expect.volatility THEN
        RAISE EXCEPTION 'FAIL: fresh fold diverges from the native scalar';
    END IF;

    -- the refuting cell lands below neutral
    SELECT * INTO row2 FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_a AND object_id = o2;
    IF row2.rating >= neutral THEN
        RAISE EXCEPTION 'FAIL: refuting evidence must sink rating below neutral';
    END IF;

    -- 2) partition routing: the hot-relation cell lives in an IS_A hash leaf.
    SELECT c.tableoid::regclass::text INTO leafname FROM laplace.consensus c
    WHERE c.subject_id = subj AND c.type_id = rel_hot AND c.object_id = o1;
    IF leafname NOT LIKE '%consensus_h%' THEN
        RAISE EXCEPTION 'FAIL: hot cell routed to %, expected an is_a hash leaf', leafname;
    END IF;

    -- 3) second batch folds against the stored prior: exact scalar parity,
    --    witness accumulation, GREATEST timestamp.
    expect := laplace.laplace_glicko2_accumulate_period(
        row1.rating, row1.rd, row1.volatility,
        ARRAY[consensus.glicko2_neutral_mu()]::bigint[], ARRAY[phi]::bigint[],
        ARRAY[5]::bigint[], ARRAY[5 * s_conf]::bigint[], consensus.glicko2_tau());
    affected := consensus.upsert(
        ARRAY[subj], ARRAY[rel_hot], ARRAY[o1],
        ARRAY[phi], ARRAY[5]::bigint[], ARRAY[5 * s_conf]::bigint[], ARRAY[t2]);
    IF affected <> 1 THEN
        RAISE EXCEPTION 'FAIL: refold affected % rows, expected 1', affected;
    END IF;
    SELECT * INTO row2 FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_hot AND object_id = o1;
    IF row2.witness_count <> 7 THEN
        RAISE EXCEPTION 'FAIL: witness_count=% after refold, expected 7', row2.witness_count;
    END IF;
    IF row2.rating <> expect.rating OR row2.rd <> expect.rd
       OR row2.volatility <> expect.volatility THEN
        RAISE EXCEPTION 'FAIL: refold diverges from the native scalar over the stored prior';
    END IF;
    IF row2.rd >= row1.rd THEN
        RAISE EXCEPTION 'FAIL: more evidence must shrink rd (%.. -> %)', row1.rd, row2.rd;
    END IF;
    IF row2.last_observed_at <> t2 THEN
        RAISE EXCEPTION 'FAIL: last_observed_at=%, expected GREATEST=%', row2.last_observed_at, t2;
    END IF;

    -- 4) a duplicate cell in one call is a contract violation and fails loud.
    BEGIN
        PERFORM consensus.upsert(
            ARRAY[subj, subj], ARRAY[rel_hot, rel_hot], ARRAY[o1, o1],
            ARRAY[phi, phi], ARRAY[1, 1]::bigint[], ARRAY[s_conf, s_conf]::bigint[],
            ARRAY[t2, t2]);
    EXCEPTION WHEN cardinality_violation OR unique_violation THEN
        dup_ok := true;
    END;
    IF NOT dup_ok THEN
        RAISE EXCEPTION 'FAIL: duplicate cell in one call must error (client-dedup contract)';
    END IF;

    RAISE NOTICE '✓ consensus_upsert: fresh fold, hot-leaf routing, prior refold parity, witness/ts accumulation, and the client-dedup contract all hold';
END $$;

-- Every mathematical input is independently varied, twice, to exercise both
-- memo hits and misses. A key omitting ANY input must disagree with the scalar.
CREATE TEMP TABLE fold_cases AS
SELECT i, public.laplace_hash128_blake3(convert_to('memo/s/' || i, 'UTF8')) s,
       CASE WHEN i % 4 = 0 THEN NULL::bytea
            ELSE public.laplace_hash128_blake3(convert_to('memo/o/' || i, 'UTF8')) END o,
       1500000000000::bigint + CASE WHEN i % 8 = 1 THEN 200000000000 ELSE 0 END r,
       -- RD=350 is the declared Glicko ceiling. Vary the memo-key input downward,
       -- not above the admissible state domain: the fail-closed kernel must reject
       -- an impossible 370 RD rather than making a parity fixture green by accepting it.
       350000000000::bigint - CASE WHEN i % 8 = 2 THEN 20000000000 ELSE 0 END rd,
       60000000::bigint + CASE WHEN i % 8 = 3 THEN 10000000 ELSE 0 END vol,
       1500000000000::bigint + CASE WHEN i % 8 = 4 THEN 300000000000 ELSE 0 END opp,
       30000000000::bigint + CASE WHEN i % 8 = 5 THEN 10000000000 ELSE 0 END phi,
       1::bigint + CASE WHEN i % 8 = 6 THEN 1 ELSE 0 END games,
       900000000::bigint - CASE WHEN i % 8 = 7 THEN 500000000 ELSE 0 END score
FROM generate_series(0,15) i;

DO $$
DECLARE
    t bytea := laplace.relation_type_id('IS_A');
    phase int;
    affected bigint;
BEGIN
    FOR phase IN 0..1 LOOP
        IF phase = 1 THEN
            UPDATE laplace.consensus c
            SET rating=f.r, rd=f.rd, volatility=f.vol
            FROM fold_cases f WHERE c.id=laplace.consensus_id(f.s,t,f.o)
                AND c.type_id=t AND c.subject_id=f.s;
        END IF;
        SELECT consensus.upsert_type(t, array_agg(s ORDER BY i), array_agg(o ORDER BY i),
            array_agg(phi ORDER BY i), array_agg(games ORDER BY i), array_agg(score ORDER BY i),
            array_agg('2026-01-01'::timestamptz ORDER BY i), array_agg(opp ORDER BY i))
        INTO affected FROM fold_cases;
        IF affected <> 16 THEN RAISE EXCEPTION 'memo fold affected %', affected; END IF;
        IF EXISTS (
            SELECT FROM fold_cases f
            JOIN laplace.consensus c ON c.id=laplace.consensus_id(f.s,t,f.o)
                AND c.type_id=t AND c.subject_id=f.s
            CROSS JOIN LATERAL laplace.laplace_glicko2_accumulate_period(
                CASE WHEN phase=0 THEN consensus.glicko2_neutral_mu() ELSE f.r END,
                CASE WHEN phase=0 THEN consensus.glicko2_initial_rd() ELSE f.rd END,
                CASE WHEN phase=0 THEN consensus.glicko2_initial_volatility() ELSE f.vol END,
                ARRAY[f.opp]::bigint[], ARRAY[f.phi]::bigint[],
                ARRAY[f.games]::bigint[], ARRAY[f.score]::bigint[],
                consensus.glicko2_tau()) expected
            WHERE (c.rating,c.rd,c.volatility) IS DISTINCT FROM
                (expected.rating,expected.rd,expected.volatility)
                OR c.witness_count <> (phase+1)*f.games
        ) THEN RAISE EXCEPTION 'batch/scalar mismatch in phase %', phase; END IF;
    END LOOP;
    RAISE NOTICE 'all seven fold inputs retain exact scalar parity';
END $$;

-- Failed native folds expose the exact effective mathematical inputs and
-- preserve the stored prior. Exact group inputs override cell placeholders.
DO $fold_failure$
DECLARE
    t bytea := laplace.relation_type_id('IS_A');
    s bytea := public.laplace_hash128_blake3('test/fold-failure/subject');
    o bytea := public.laplace_hash128_blake3('test/fold-failure/object');
    prior jsonb;
    after_failure jsonb;
    detail text;
    message text;
    refused boolean := false;
BEGIN
    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd, volatility,
         witness_count, last_observed_at)
    VALUES
        (laplace.consensus_id(s,t,o),s,t,o,1800000000000,80000000000,70000000,
         7,'2026-01-01 00:00:00+00');
    SELECT to_jsonb(c) INTO STRICT prior FROM laplace.consensus c
    WHERE c.id=laplace.consensus_id(s,t,o) AND c.type_id=t AND c.subject_id=s;
    BEGIN
        -- A score larger than the one-game maximum deterministically fails
        -- inside the kernel without depending on a convergence edge case.
        PERFORM consensus.upsert_type(t,ARRAY[s],ARRAY[o],
            ARRAY[330000000000]::bigint[],ARRAY[1]::bigint[],
            ARRAY[1000000001]::bigint[],ARRAY['2026-02-01 00:00:00+00'::timestamptz],
            ARRAY[1900000000000]::bigint[],ARRAY[0,1]::bigint[],
            ARRAY[1600000000000]::bigint[],ARRAY[70000000000]::bigint[],
            ARRAY[1]::bigint[],ARRAY[1000000001]::bigint[]);
    EXCEPTION WHEN numeric_value_out_of_range THEN
        GET STACKED DIAGNOSTICS detail=PG_EXCEPTION_DETAIL, message=MESSAGE_TEXT;
        refused := true;
        IF message IS DISTINCT FROM 'consensus_upsert_type: native rating-period update failed'
           OR detail IS NULL
           OR detail NOT LIKE 'result=-1 cell_index=0 matched=true exact_period=true period_start=0 prior_rating=1800000000000 prior_rd=80000000000 prior_volatility=70000000 opponent_rating=1600000000000 opponent_rd=70000000000 games=1 sum_score=1000000001 tau=500000000%'
           OR detail NOT LIKE ('%backend_pid=' || pg_backend_pid()::text || ' %')
           OR detail NOT LIKE '%core_mapping=[%laplace_core%] route_address=%'
           OR (detail NOT LIKE '%route_mapping=[%'
               OR (detail NOT LIKE '%laplace_substrate%'
                   AND position('laplace_execution_' in detail) = 0)) THEN
            RAISE EXCEPTION 'native fold failure lost its exact effective input context: % / %',
                message, detail;
        END IF;
    END;
    SELECT to_jsonb(c) INTO STRICT after_failure FROM laplace.consensus c
    WHERE c.id=laplace.consensus_id(s,t,o) AND c.type_id=t AND c.subject_id=s;
    IF NOT refused OR after_failure IS DISTINCT FROM prior THEN
        RAISE EXCEPTION 'failed native fold accepted invalid evidence or changed the stored prior';
    END IF;
END $fold_failure$;

ROLLBACK;
