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

ROLLBACK;