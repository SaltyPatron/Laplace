CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

BEGIN;

DO $$
DECLARE
    type_t       bytea := public.laplace_hash128_blake3(convert_to('Type', 'UTF8'));
    type_word    bytea := public.laplace_hash128_blake3(convert_to('Word', 'UTF8'));
    type_sent    bytea := public.laplace_hash128_blake3(convert_to('Sentence', 'UTF8'));
    src          bytea := public.laplace_hash128_blake3(convert_to('test/steer/source', 'UTF8'));
    ctx          bytea := public.laplace_hash128_blake3(convert_to('test/steer/context', 'UTF8'));
    noise        bytea := public.laplace_hash128_blake3(convert_to('test/steer/noise', 'UTF8'));
    semantic     bytea := public.laplace_hash128_blake3(convert_to('test/steer/semantic', 'UTF8'));
    frontier     bytea := public.laplace_hash128_blake3(convert_to('test/steer/frontier', 'UTF8'));
    unrelated    bytea := public.laplace_hash128_blake3(convert_to('test/steer/unrelated', 'UTF8'));
    sent_noise   bytea := public.laplace_hash128_blake3(convert_to('test/steer/sentence-noise', 'UTF8'));
    sent_sem     bytea := public.laplace_hash128_blake3(convert_to('test/steer/sentence-semantic', 'UTF8'));
    rel          bytea := laplace.relation_type_id('IS_A');
    t2flag       bigint := (2::bigint << 1);
    picked       bytea;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
    VALUES
        (src, 0, type_t, NULL),
        (ctx, 2, type_word, src),
        (noise, 2, type_word, src),
        (semantic, 2, type_word, src),
        (frontier, 2, type_word, src),
        (unrelated, 2, type_word, src),
        (sent_noise, 3, type_sent, src),
        (sent_sem, 3, type_sent, src),
        (rel, 0, laplace.entity_type_id('RelationType'), src)
    ON CONFLICT DO NOTHING;

    -- S6 sees a very strong sequence prior for noise: thirty-two distinct
    -- physical observations carry ctx -> noise, while only one carries the
    -- semantically relevant ctx -> semantic continuation.
    FOR i IN 1..32 LOOP
        INSERT INTO laplace.physicalities
            (id, entity_id, type, coord, hilbert_index, trajectory,
             n_constituents, observed_at)
        VALUES
            (public.laplace_hash128_blake3(
                 convert_to('test/steer/noise-observation/' || i::text, 'UTF8')),
             sent_noise, 1,
             public.ST_SetSRID(public.ST_MakePoint(i, 1, 1, 1), 0),
             decode('00000000000000000000000000000000', 'hex'),
             public.ST_MakeLine(ARRAY[
                 public.laplace_mantissa_pack(ctx, 1, 1, t2flag),
                 public.laplace_mantissa_pack(noise, 2, 1, t2flag)]),
             2, now());
    END LOOP;

    INSERT INTO laplace.physicalities
        (id, entity_id, type, coord, hilbert_index, trajectory,
         n_constituents, observed_at)
    VALUES
        (public.laplace_hash128_blake3(convert_to('test/steer/semantic-observation', 'UTF8')),
         sent_sem, 1,
         public.ST_SetSRID(public.ST_MakePoint(100, 1, 1, 1), 0),
         decode('00000000000000000000000000000000', 'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(ctx, 1, 1, t2flag),
             public.laplace_mantissa_pack(semantic, 2, 1, t2flag)]),
         2, now());

    -- S7 has positive witnessed meaning for semantic and no opinion about noise.
    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd,
         volatility, witness_count, last_observed_at)
    VALUES
        (laplace.consensus_id(semantic, rel, frontier),
         semantic, rel, frontier,
         2000000000000, 30000000000, 60000000, 5, now());

    IF NOT EXISTS (
        SELECT 1
        FROM generation.steer_candidates(ARRAY[noise, semantic], ARRAY[frontier]) s
        WHERE s.candidate = semantic AND s.edges > 0 AND s.steer > 0.0) THEN
        RAISE EXCEPTION 'FAIL: semantic candidate did not receive positive witnessed steering';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM generation.steer_candidates(ARRAY[noise, semantic], ARRAY[frontier]) s
        WHERE s.candidate = noise AND s.edges = 0) THEN
        RAISE EXCEPTION 'FAIL: sequence-only candidate was not preserved as unattested';
    END IF;

    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier]) g;
    IF picked IS DISTINCT FROM semantic THEN
        RAISE EXCEPTION 'FAIL: unattested sequence frequency outranked positive S7 meaning';
    END IF;

    -- No positive semantic signal: unattested remains a legitimate sequence
    -- fallback rather than being reclassified as refuted.
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[unrelated]) g;
    IF picked IS DISTINCT FROM noise THEN
        RAISE EXCEPTION 'FAIL: unattested sequence fallback was lost when S7 had no positive signal';
    END IF;

    RAISE NOTICE '✓ steering precedence: positive witnessed meaning outranks unattested frequency; unattested remains fallback';
END $$;

ROLLBACK;
