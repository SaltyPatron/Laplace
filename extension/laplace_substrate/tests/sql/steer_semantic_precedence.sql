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
    semantic     bytea := laplace.word_id('ββ');
    frontier     bytea := public.laplace_hash128_blake3(convert_to('test/steer/frontier', 'UTF8'));
    unrelated    bytea := public.laplace_hash128_blake3(convert_to('test/steer/unrelated', 'UTF8'));
    sent_noise   bytea := public.laplace_hash128_blake3(convert_to('test/steer/sentence-noise', 'UTF8'));
    sent_sem     bytea := public.laplace_hash128_blake3(convert_to('test/steer/sentence-semantic', 'UTF8'));
    rel          bytea := laplace.relation_type_id('IS_A');
    t2flag       bigint := (2::bigint << 1);
    picked       bytea;
    picked_stride integer;
    gap          bytea := laplace.word_id(' ');
    scoped_weight bigint;
    scoped_steps bytea[];
    next_root bytea := public.laplace_hash128_blake3('test/steer/next-observation');
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

    -- The semantic output has actual canonical content, independent of every
    -- sequence context. Admission never classifies candidates by rendered text.
    INSERT INTO laplace.physicalities
        (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents, observed_at)
    VALUES
        (public.laplace_hash128_blake3(convert_to('test/steer/content-identity', 'UTF8')),
         semantic, 1, public.ST_SetSRID(public.ST_MakePoint(101, 1, 1, 1), 0),
         decode('00000000000000000000000000000000', 'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(laplace.word_id('β'), 1, 1, 0),
             public.laplace_mantissa_pack(laplace.word_id('β'), 2, 1, 0)]),
         2, now());
    IF realize.render_text(semantic) IS DISTINCT FROM 'ββ' THEN
        RAISE EXCEPTION 'FAIL: semantic fixture is not realizable canonical content';
    END IF;

    -- S7 has positive witnessed meaning for semantic and no opinion about noise.
    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd,
         volatility, witness_count, last_observed_at)
    VALUES
        (laplace.consensus_id(frontier, rel, semantic),
         frontier, rel, semantic,
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

    -- Remove every ctx -> semantic sequence observation. S6 sequence now knows
    -- only noise, yet the independently witnessed semantic neighbor must enter
    -- the union before S7 and be selected with no fabricated suffix evidence.
    DELETE FROM laplace.physicalities WHERE entity_id = sent_sem;
    IF EXISTS (SELECT 1 FROM generation.trajectory_continuations(ARRAY[ctx], NULL)
               WHERE object_id = semantic) THEN
        RAISE EXCEPTION 'FAIL: semantic candidate still exists in sequence proposals';
    END IF;
    SELECT g.entity, g.stride_used INTO picked, picked_stride
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier]) g;
    IF picked IS DISTINCT FROM semantic OR picked_stride IS DISTINCT FROM 0 THEN
        RAISE EXCEPTION 'FAIL: witnessed semantic-only proposal missing or attributed a sequence stride';
    END IF;

    -- Routed semantic content is eligible even when COMPOSE already put it in
    -- the frontier. It is not a prompt seed or fabricated sequence constituent.
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier, semantic]) g;
    IF picked IS DISTINCT FROM semantic THEN
        RAISE EXCEPTION 'FAIL: routed output content was mistaken for a prompt seed';
    END IF;
    IF (SELECT count(*) FROM generation.forward_walk_continuations(
            ARRAY[ctx], 3, 1, 0.0, 8, 7, ARRAY[frontier, semantic])) <> 1 THEN
        RAISE EXCEPTION 'FAIL: semantic-only graph cycle repeated content without sequence testimony';
    END IF;

    -- A declared asymmetric operation must not reverse its operands. The
    -- reverse semantic -> frontier IS_A evidence is not frontier -> semantic.
    DELETE FROM laplace.consensus WHERE subject_id = frontier AND type_id = rel;
    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES (laplace.consensus_id(semantic,rel,frontier),semantic,rel,frontier,
            2000000000000,30000000000,60000000,5,now());
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier], ARRAY[rel]) g;
    IF picked IS DISTINCT FROM noise THEN
        RAISE EXCEPTION 'FAIL: typed operation reversed asymmetric testimony';
    END IF;

    DELETE FROM laplace.consensus WHERE subject_id = semantic AND type_id = rel;
    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES (laplace.consensus_id(frontier,rel,semantic),frontier,rel,semantic,
            2000000000000,30000000000,60000000,5,now());

    -- Stronger evidence for another operation cannot override the requested
    -- relation. Both proposals pass through the same native score and sampler.
    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd,
         volatility, witness_count, last_observed_at)
    VALUES
        (laplace.consensus_id(frontier, rel, noise), frontier, rel, noise,
         2500000000000, 30000000000, 60000000, 5, now()),
        (laplace.consensus_id(frontier, laplace.relation_type_id('HAS_PART'), semantic),
         frontier, laplace.relation_type_id('HAS_PART'), semantic,
         1800000000000, 30000000000, 60000000, 5, now());
    SELECT g.entity, g.stride_used INTO picked, picked_stride
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier],
             ARRAY[laplace.relation_type_id('HAS_PART')]) g;
    IF picked IS DISTINCT FROM semantic OR picked_stride IS DISTINCT FROM 0 THEN
        RAISE EXCEPTION 'FAIL: typed operand lost to unrelated relation mass';
    END IF;
    DELETE FROM laplace.consensus WHERE subject_id = frontier
        AND (object_id = noise OR type_id = laplace.relation_type_id('HAS_PART'));

    -- The canonical relation manifest owns symmetry: the same typed operation
    -- may traverse reverse-stored evidence when its relation is symmetric.
    INSERT INTO laplace.consensus
        (id, subject_id, type_id, object_id, rating, rd,
         volatility, witness_count, last_observed_at)
    VALUES
        (laplace.consensus_id(semantic, laplace.relation_type_id('IS_ANTONYM_OF'), frontier),
         semantic, laplace.relation_type_id('IS_ANTONYM_OF'), frontier,
         1800000000000, 30000000000, 60000000, 5, now());
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier],
             ARRAY[laplace.relation_type_id('IS_ANTONYM_OF')]) g;
    IF picked IS DISTINCT FROM semantic THEN
        RAISE EXCEPTION 'FAIL: typed operation lost reverse-stored symmetric evidence';
    END IF;
    DELETE FROM laplace.consensus WHERE subject_id = semantic AND type_id <> rel;

    -- A genuinely refuted semantic proposal must not become a fallback merely
    -- because it was nominated by the neighborhood index.
    UPDATE laplace.consensus SET rating = 1000000000000
    WHERE subject_id = frontier AND type_id = rel AND object_id = semantic;
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[frontier]) g;
    IF picked IS DISTINCT FROM noise THEN
        RAISE EXCEPTION 'FAIL: refuted semantic-only proposal erased legitimate sequence fallback';
    END IF;

    -- No positive semantic signal: unattested remains a legitimate sequence
    -- fallback rather than being reclassified as refuted.
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             ARRAY[ctx], 1, 1, 0.0, 8, 7, ARRAY[unrelated]) g;
    IF picked IS DISTINCT FROM noise THEN
        RAISE EXCEPTION 'FAIL: unattested sequence fallback was lost when S7 had no positive signal';
    END IF;

    -- An all-NULL route falls back to every prompt constituent, even when that
    -- prompt is much longer than the rolling window's allocation.
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
             array_fill(ctx, ARRAY[64]), 1, 1, 0.0, 8, 7, ARRAY[NULL]::bytea[]) g;
    IF picked IS DISTINCT FROM noise THEN
        RAISE EXCEPTION 'FAIL: missing route lost a long prompt fallback';
    END IF;

    -- A missed five-ID context must retain a four-ID witnessed suffix even
    -- when the indexed fallback probes lengths 3, 2, 1. Repeated words and
    -- separators remain real ordered occurrences, including one-ID fallback.
    INSERT INTO laplace.physicalities
        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
    VALUES
        (public.laplace_hash128_blake3('test/steer/suffix-long'),sent_noise,1,
         public.ST_MakePoint(101,1,1,1),decode(repeat('00',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(ctx,1,1,t2flag),
             public.laplace_mantissa_pack(gap,2,1,0),
             public.laplace_mantissa_pack(ctx,3,1,t2flag),
             public.laplace_mantissa_pack(gap,4,1,0),
             public.laplace_mantissa_pack(noise,5,1,t2flag)]),5,now()),
        (public.laplace_hash128_blake3('test/steer/suffix-short'),sent_sem,1,
         public.ST_MakePoint(102,1,1,1),decode(repeat('00',16),'hex'),
         public.ST_MakeLine(ARRAY[
             public.laplace_mantissa_pack(ctx,1,1,t2flag),
             public.laplace_mantissa_pack(gap,2,1,0),
             public.laplace_mantissa_pack(semantic,3,1,t2flag)]),3,now());
    SELECT g.entity,g.stride_used INTO picked,picked_stride
    FROM generation.forward_walk_continuations(
        ARRAY[unrelated,ctx,gap,ctx,gap],1,5,0.0,8,7,ARRAY[unrelated]) g;
    IF picked IS DISTINCT FROM noise OR picked_stride IS DISTINCT FROM 4 THEN
        RAISE EXCEPTION 'FAIL: indexed suffix fallback skipped the greatest witnessed stride';
    END IF;
    SELECT g.stride_used INTO picked_stride
    FROM generation.forward_walk_continuations(
        ARRAY[unrelated,frontier,unrelated,ctx,gap],1,5,0.0,8,7,ARRAY[unrelated]) g;
    IF picked_stride IS DISTINCT FROM 2 THEN
        RAISE EXCEPTION 'FAIL: indexed suffix fallback lost the word/separator pair';
    END IF;
    SELECT g.stride_used INTO picked_stride
    FROM generation.forward_walk_continuations(
        ARRAY[unrelated,frontier,unrelated,frontier,gap],1,5,0.0,8,7,ARRAY[unrelated]) g;
    IF picked_stride IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'FAIL: indexed suffix fallback discarded the separator identity';
    END IF;

    -- The observation operand bounds sequence support before matching. The
    -- thirty-two unrelated ctx -> noise trajectories must not enter through
    -- suffix backoff. Empty scope must also remain empty.
    -- Canonical Content identity is BLAKE3(entity_id || uint16_le(1)); the
    -- earlier synthetic corpus rows deliberately model separate observations.
    INSERT INTO laplace.physicalities
        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
    VALUES (public.laplace_hash128_blake3(sent_sem||decode('0100','hex')),sent_sem,1,
        public.ST_MakePoint(104,1,1,1),decode(repeat('00',16),'hex'),
        public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(ctx,1,1,t2flag),
                                public.laplace_mantissa_pack(gap,2,1,0),
                                public.laplace_mantissa_pack(semantic,3,1,t2flag)]),3);
    SELECT g.entity,g.stride_used INTO picked,picked_stride
    FROM generation.forward_walk_continuations(
        ARRAY[ctx,gap],1,5,0.0,8,7,ARRAY[unrelated], '{}'::bytea[],8,
        ARRAY[sent_sem,sent_sem]) g;
    IF picked IS DISTINCT FROM semantic OR picked_stride IS DISTINCT FROM 2 THEN
        RAISE EXCEPTION 'FAIL: scoped continuation escaped its observed root or lost SPACE';
    END IF;
    SELECT g.entity INTO picked
    FROM generation.forward_walk_continuations(
        ARRAY[ctx],1,5,0.0,8,7,ARRAY[unrelated], '{}'::bytea[],8,'{}'::bytea[]) g;
    IF picked IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: empty observation scope reopened the corpus';
    END IF;

    -- A witnessed context binds a trajectory without pretending that the
    -- context itself is a sequence position. A shared root is read once even
    -- when several attestations reach it.
    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,context_id,outcome,
         last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES
        (public.laplace_hash128_blake3('test/steer/scoped-witness'),
         unrelated,rel,semantic,src,sent_sem,1,now(),1,1000000000,350000000000),
        (public.laplace_hash128_blake3('test/steer/scoped-witness-2'),
         unrelated,rel,ctx,src,sent_sem,1,now(),1,1000000000,350000000000);
    SELECT g.entity,g.stride_used INTO picked,picked_stride
    FROM generation.forward_walk_continuations(
        ARRAY[ctx,gap],1,5,0.0,8,7,ARRAY[unrelated], '{}'::bytea[],8,
        ARRAY[unrelated,unrelated]) g;
    IF picked IS DISTINCT FROM semantic OR picked_stride IS DISTINCT FROM 2 THEN
        RAISE EXCEPTION 'FAIL: witnessed context did not bind its ordered physicality';
    END IF;
    SELECT g.weight INTO scoped_weight
    FROM generation.trajectory_continuations(ARRAY[ctx,gap],NULL,
        ARRAY[unrelated,unrelated,sent_sem]) g WHERE g.object_id=semantic;
    IF scoped_weight IS DISTINCT FROM 1::bigint THEN
        RAISE EXCEPTION 'FAIL: duplicate operands/witness roots inflated physical occurrence count';
    END IF;

    -- Selecting an identity makes its observations available to the next
    -- step. This root was not in the initial operand's evidence.
    INSERT INTO laplace.physicalities
        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
    VALUES (public.laplace_hash128_blake3(next_root||decode('0100','hex')),next_root,1,
        public.ST_MakePoint(103,1,1,1),decode(repeat('00',16),'hex'),
        public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(semantic,1,1,t2flag),
                                public.laplace_mantissa_pack(noise,2,1,t2flag)]),2);
    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,context_id,outcome,
         last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES (public.laplace_hash128_blake3('test/steer/selected-witness'),
        semantic,rel,noise,src,next_root,1,now(),1,1000000000,350000000000);
    SELECT array_agg(g.entity ORDER BY g.step) INTO scoped_steps
    FROM generation.forward_walk_continuations(
        ARRAY[ctx,gap],2,5,0.0,8,7,ARRAY[unrelated], '{}'::bytea[],8,ARRAY[unrelated]) g;
    IF scoped_steps IS DISTINCT FROM ARRAY[semantic,noise] THEN
        RAISE EXCEPTION 'FAIL: selected identity did not extend the next observation scope';
    END IF;

    RAISE NOTICE '✓ steering precedence: positive witnessed meaning enters without sequence evidence; refutation preserves legitimate fallback';
END $$;

ROLLBACK;
