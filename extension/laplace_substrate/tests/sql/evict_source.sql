-- evict_source (GH #508): deposit v1 (witness + analysis sources) -> evict the
-- analysis source -> cells with only-analysis evidence are GONE (not zeroed),
-- mixed cells are refolded byte-identically to the canonical fold of their
-- surviving rows, untouched cells are byte-identical, markers are deleted,
-- touched entities are queued for mask repair -> re-derive v2 -> witness counts
-- equal the v1 snapshot (no double count). Unseeded evict is a no-op, not an
-- error. The procedure COMMITs per batch, so this file runs outside an explicit
-- transaction and cleans up after itself.

DO $$
DECLARE
    type_t   bytea := public.laplace_hash128_blake3('Type');
    src_w    bytea := laplace.source_id('EvictTestWitness');
    src_a    bytea := laplace.source_id('EvictTestAnalysis');
    marker_t bytea := laplace.entity_type_id('Evict_TestMarker');
    rel_p    bytea := public.laplace_hash128_blake3('test/evict/rel');
    rel_hot  bytea := laplace.relation_type_id('IS_A');
    subj     bytea := public.laplace_hash128_blake3('test/evict/subject');
    o1       bytea := public.laplace_hash128_blake3('test/evict/obj1');
    o2       bytea := public.laplace_hash128_blake3('test/evict/obj2');
    o3       bytea := public.laplace_hash128_blake3('test/evict/obj3');
    o4       bytea := public.laplace_hash128_blake3('test/evict/obj4');
    o5       bytea := public.laplace_hash128_blake3('test/evict/obj5');
    m1       bytea := public.laplace_hash128_blake3('test/evict/marker1');
    m2       bytea := public.laplace_hash128_blake3('test/evict/marker2');
    m3       bytea := public.laplace_hash128_blake3('test/evict/content-by-analysis');
    m4       bytea := public.laplace_hash128_blake3('test/evict/marker-of-witness');
    phi_w    bigint := 30000000000;
    phi_a    bigint := 150000000000;
    win      bigint := 1000000000;
    s_conf   bigint := 900000000;
    s_ref    bigint := 100000000;
    t1       timestamptz := '2026-01-01 00:00:00+00';
    t2       timestamptz := '2026-02-01 00:00:00+00';
    t3       timestamptz := '2026-03-01 00:00:00+00';
    affected bigint;
BEGIN
    INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES
        (src_w, 0, type_t, NULL), (src_a, 0, type_t, NULL),
        (rel_p, 0, type_t, src_w), (subj, 0, type_t, src_w),
        (o1, 0, type_t, src_w), (o2, 0, type_t, src_w), (o3, 0, type_t, src_w),
        (o4, 0, type_t, src_w), (o5, 0, type_t, src_w),
        -- m1/m2: the analysis lane's derivation-gate markers (deleted by evict).
        -- m3: CONTENT first-observed by the analysis source (must survive).
        -- m4: a marker-typed entity of ANOTHER source (must survive).
        (m1, 4, marker_t, src_a), (m2, 4, marker_t, src_a),
        (m3, 4, type_t, src_a), (m4, 4, marker_t, src_w)
    ON CONFLICT (id, tier) DO NOTHING;

    -- Evidence rows persist the fold's exact inputs (observation_count,
    -- sum_score_fp1e9, opponent_rd_fp1e9) — the refold replays them verbatim.
    INSERT INTO laplace.attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count,
         sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES
        -- cell I (subj, rel_p, o1): ANALYSIS ONLY — must be culled entirely
        (public.laplace_hash128_blake3('test/evict/a1'), subj, rel_p, o1, src_a, NULL,
         2, t2, 2, 2 * s_conf, phi_a),
        (public.laplace_hash128_blake3('test/evict/a2'), subj, rel_p, o1, src_a, NULL,
         1, t3, 1, s_ref, phi_a),
        -- cell II (subj, rel_p, o2): MIXED — witness survives, cell refolds
        (public.laplace_hash128_blake3('test/evict/w1'), subj, rel_p, o2, src_w, NULL,
         2, t1, 3, 3 * s_conf, phi_w),
        (public.laplace_hash128_blake3('test/evict/a3'), subj, rel_p, o2, src_a, NULL,
         2, t2, 2, s_conf, phi_a),
        -- cell III (subj, rel_p, o3): WITNESS ONLY — untouched, byte-identical
        (public.laplace_hash128_blake3('test/evict/w2'), subj, rel_p, o3, src_w, NULL,
         2, t1, 4, 2 * win, phi_w),
        -- cell IV (subj, IS_A, o4): MIXED on a HOT (hash-partitioned) relation
        (public.laplace_hash128_blake3('test/evict/w3'), subj, rel_hot, o4, src_w, NULL,
         2, t1, 2, 2 * s_conf, phi_w),
        (public.laplace_hash128_blake3('test/evict/a4'), subj, rel_hot, o4, src_a, NULL,
         0, t2, 1, 0, phi_a),
        -- cell V (subj, rel_p, NULL): ANALYSIS ONLY, NULL object — culled
        (public.laplace_hash128_blake3('test/evict/a5'), subj, rel_p, NULL, src_a, NULL,
         2, t2, 3, 3 * win, phi_a),
        -- cell VI (subj, rel_p, o5): interleaved periods — refold order pin:
        -- surviving witness rows t1 and t3 straddle the deleted analysis row t2
        (public.laplace_hash128_blake3('test/evict/w4'), subj, rel_p, o5, src_w, NULL,
         2, t1, 1, win, phi_w),
        (public.laplace_hash128_blake3('test/evict/a6'), subj, rel_p, o5, src_a, NULL,
         2, t2, 2, 2 * s_conf, phi_a),
        (public.laplace_hash128_blake3('test/evict/w5'), subj, rel_p, o5, src_w, NULL,
         1, t3, 2, s_conf, phi_w);

    -- Consensus built exactly as ingest builds it: one incremental fold per
    -- batch against the stored prior, batches in timestamp order.
    affected := consensus.upsert(
        ARRAY[subj, subj, subj, subj],
        ARRAY[rel_p, rel_p, rel_hot, rel_p],
        ARRAY[o2, o3, o4, o5],
        ARRAY[phi_w, phi_w, phi_w, phi_w],
        ARRAY[3, 4, 2, 1]::bigint[],
        ARRAY[3 * s_conf, 2 * win, 2 * s_conf, win]::bigint[],
        ARRAY[t1, t1, t1, t1]);
    IF affected <> 4 THEN
        RAISE EXCEPTION 'FAIL: witness batch affected %, expected 4', affected;
    END IF;
    affected := consensus.upsert(
        ARRAY[subj, subj, subj, subj, subj],
        ARRAY[rel_p, rel_p, rel_hot, rel_p, rel_p],
        ARRAY[o1, o2, o4, NULL, o5],
        ARRAY[phi_a, phi_a, phi_a, phi_a, phi_a],
        ARRAY[2, 2, 1, 3, 2]::bigint[],
        ARRAY[2 * s_conf, s_conf, 0, 3 * win, 2 * s_conf]::bigint[],
        ARRAY[t2, t2, t2, t2, t2]);
    IF affected <> 5 THEN
        RAISE EXCEPTION 'FAIL: analysis batch affected %, expected 5', affected;
    END IF;
    affected := consensus.upsert(
        ARRAY[subj, subj],
        ARRAY[rel_p, rel_p],
        ARRAY[o1, o5],
        ARRAY[phi_a, phi_w],
        ARRAY[1, 2]::bigint[],
        ARRAY[s_ref, s_conf]::bigint[],
        ARRAY[t3, t3]);
    IF affected <> 2 THEN
        RAISE EXCEPTION 'FAIL: t3 batch affected %, expected 2', affected;
    END IF;

    -- v1 snapshot: every cell of the fixture subject.
    CREATE TEMP TABLE _ev_snap_v1 AS
    SELECT c.subject_id, c.type_id, c.object_id, c.rating, c.rd, c.volatility,
           c.witness_count, c.last_observed_at
    FROM laplace.consensus c WHERE c.subject_id = subj;
    IF (SELECT count(*) FROM _ev_snap_v1) <> 6 THEN
        RAISE EXCEPTION 'FAIL: v1 snapshot has % cells, expected 6',
            (SELECT count(*) FROM _ev_snap_v1);
    END IF;

    RAISE NOTICE '- evict fixture deposited: 6 cells, 12 evidence rows, 2 markers';
END $$;

-- The eviction under test. p_batch = 2 forces multiple delete/refold batches per
-- relation; p_drain = false leaves highway_mask_dirty observable so its exact
-- contents can be asserted before the logger-bearing drain path is exercised below.
CALL ops.evict_source(laplace.source_id('EvictTestAnalysis'), NULL,
                  ARRAY[laplace.entity_type_id('Evict_TestMarker')],
                  p_batch => 2, p_drain => false);

DO $$
DECLARE
    src_w    bytea := laplace.source_id('EvictTestWitness');
    src_a    bytea := laplace.source_id('EvictTestAnalysis');
    marker_t bytea := laplace.entity_type_id('Evict_TestMarker');
    rel_p    bytea := public.laplace_hash128_blake3('test/evict/rel');
    rel_hot  bytea := laplace.relation_type_id('IS_A');
    subj     bytea := public.laplace_hash128_blake3('test/evict/subject');
    o1       bytea := public.laplace_hash128_blake3('test/evict/obj1');
    o2       bytea := public.laplace_hash128_blake3('test/evict/obj2');
    o3       bytea := public.laplace_hash128_blake3('test/evict/obj3');
    o4       bytea := public.laplace_hash128_blake3('test/evict/obj4');
    o5       bytea := public.laplace_hash128_blake3('test/evict/obj5');
    m1       bytea := public.laplace_hash128_blake3('test/evict/marker1');
    m2       bytea := public.laplace_hash128_blake3('test/evict/marker2');
    m3       bytea := public.laplace_hash128_blake3('test/evict/content-by-analysis');
    m4       bytea := public.laplace_hash128_blake3('test/evict/marker-of-witness');
    phi_w    bigint := 30000000000;
    win      bigint := 1000000000;
    s_conf   bigint := 900000000;
    t1       timestamptz := '2026-01-01 00:00:00+00';
    t3       timestamptz := '2026-03-01 00:00:00+00';
    n        bigint;
    row_c    laplace.consensus%ROWTYPE;
    snap     _ev_snap_v1%ROWTYPE;
    expect   laplace.laplace_glicko2_result;
    step     laplace.laplace_glicko2_result;
    leafname text;
BEGIN
    -- every evidence row of the evicted source is gone; the witness layer is intact
    SELECT count(*) INTO n FROM laplace.attestations WHERE source_id = src_a;
    IF n <> 0 THEN RAISE EXCEPTION 'FAIL: % analysis rows survived eviction', n; END IF;
    SELECT count(*) INTO n FROM laplace.attestations WHERE source_id = src_w;
    IF n <> 5 THEN RAISE EXCEPTION 'FAIL: witness evidence count % <> 5', n; END IF;

    -- (I) and (V): zero-survivor cells are DELETED, not zeroed (unattested is not
    -- attested-false)
    IF EXISTS (SELECT 1 FROM laplace.consensus WHERE subject_id = subj AND type_id = rel_p
               AND object_id = o1) THEN
        RAISE EXCEPTION 'FAIL: analysis-only cell survived as a zeroed row';
    END IF;
    IF EXISTS (SELECT 1 FROM laplace.consensus WHERE subject_id = subj AND type_id = rel_p
               AND object_id IS NULL) THEN
        RAISE EXCEPTION 'FAIL: NULL-object analysis-only cell survived';
    END IF;

    -- (III): untouched witnessed cell is byte-identical to the v1 snapshot
    SELECT * INTO row_c FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_p AND object_id = o3;
    SELECT * INTO snap FROM _ev_snap_v1
    WHERE subject_id = subj AND type_id = rel_p AND object_id = o3;
    IF row_c.rating <> snap.rating OR row_c.rd <> snap.rd
       OR row_c.volatility <> snap.volatility
       OR row_c.witness_count <> snap.witness_count
       OR row_c.last_observed_at <> snap.last_observed_at THEN
        RAISE EXCEPTION 'FAIL: untouched cell mutated by eviction';
    END IF;

    -- (II): mixed cell refolds to the canonical fold of its surviving row — the
    -- same native scalar, neutral prior
    expect := laplace.laplace_glicko2_accumulate_games(
        consensus.glicko2_neutral_mu(), consensus.glicko2_initial_rd(), consensus.glicko2_initial_volatility(),
        consensus.glicko2_neutral_mu(), phi_w, 3, 3 * s_conf, consensus.glicko2_tau());
    SELECT * INTO row_c FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_p AND object_id = o2;
    IF NOT FOUND THEN RAISE EXCEPTION 'FAIL: mixed cell culled'; END IF;
    IF row_c.rating <> expect.rating OR row_c.rd <> expect.rd
       OR row_c.volatility <> expect.volatility THEN
        RAISE EXCEPTION 'FAIL: mixed-cell refold diverges from the native scalar';
    END IF;
    IF row_c.witness_count <> 3 OR row_c.last_observed_at <> t1 THEN
        RAISE EXCEPTION 'FAIL: mixed-cell witness_count/ts (%, %)',
            row_c.witness_count, row_c.last_observed_at;
    END IF;

    -- (IV): same law on a HOT relation; the refolded row stays in its hash leaf
    expect := laplace.laplace_glicko2_accumulate_games(
        consensus.glicko2_neutral_mu(), consensus.glicko2_initial_rd(), consensus.glicko2_initial_volatility(),
        consensus.glicko2_neutral_mu(), phi_w, 2, 2 * s_conf, consensus.glicko2_tau());
    SELECT * INTO row_c FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_hot AND object_id = o4;
    IF NOT FOUND THEN RAISE EXCEPTION 'FAIL: hot mixed cell culled'; END IF;
    IF row_c.rating <> expect.rating OR row_c.rd <> expect.rd
       OR row_c.volatility <> expect.volatility OR row_c.witness_count <> 2 THEN
        RAISE EXCEPTION 'FAIL: hot-cell refold diverges from the native scalar';
    END IF;
    SELECT c.tableoid::regclass::text INTO leafname FROM laplace.consensus c
    WHERE c.subject_id = subj AND c.type_id = rel_hot AND c.object_id = o4;
    IF leafname NOT LIKE '%consensus_r_is_a_h%' THEN
        RAISE EXCEPTION 'FAIL: refolded hot cell in %, expected an is_a hash leaf', leafname;
    END IF;

    -- (VI): canonical order pin — survivors refold by (last_observed_at, id),
    -- t1 then t3, regardless of the deleted t2 period between them
    step := laplace.laplace_glicko2_accumulate_games(
        consensus.glicko2_neutral_mu(), consensus.glicko2_initial_rd(), consensus.glicko2_initial_volatility(),
        consensus.glicko2_neutral_mu(), phi_w, 1, win, consensus.glicko2_tau());
    expect := laplace.laplace_glicko2_accumulate_games(
        step.rating, step.rd, step.volatility,
        consensus.glicko2_neutral_mu(), phi_w, 2, s_conf, consensus.glicko2_tau());
    SELECT * INTO row_c FROM laplace.consensus
    WHERE subject_id = subj AND type_id = rel_p AND object_id = o5;
    IF NOT FOUND THEN RAISE EXCEPTION 'FAIL: interleaved cell culled'; END IF;
    IF row_c.rating <> expect.rating OR row_c.rd <> expect.rd
       OR row_c.volatility <> expect.volatility THEN
        RAISE EXCEPTION 'FAIL: interleaved refold broke the canonical (ts, id) order';
    END IF;
    IF row_c.witness_count <> 3 OR row_c.last_observed_at <> t3 THEN
        RAISE EXCEPTION 'FAIL: interleaved witness_count/ts (%, %)',
            row_c.witness_count, row_c.last_observed_at;
    END IF;

    -- markers: the evicted source's gates are gone; content it first observed and
    -- other sources' markers survive
    IF EXISTS (SELECT 1 FROM laplace.entities WHERE id IN (m1, m2)) THEN
        RAISE EXCEPTION 'FAIL: analysis markers survived eviction';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM laplace.entities WHERE id = m3) THEN
        RAISE EXCEPTION 'FAIL: content entity deleted — content is never evicted';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM laplace.entities WHERE id = m4) THEN
        RAISE EXCEPTION 'FAIL: another source''s marker deleted';
    END IF;

    -- mask repair queue: exactly the touched entities (subjects + non-NULL objects
    -- of deleted evidence), ready for highway_mask_drain
    SELECT count(*) INTO n FROM laplace.highway_mask_dirty
    WHERE id IN (subj, o1, o2, o4, o5);
    IF n <> 5 THEN
        RAISE EXCEPTION 'FAIL: mask queue holds % of the 5 touched entities', n;
    END IF;
    IF EXISTS (SELECT 1 FROM laplace.highway_mask_dirty WHERE id = o3) THEN
        RAISE EXCEPTION 'FAIL: untouched entity queued for mask repair';
    END IF;

    -- post-evict snapshot for the idempotence check below
    CREATE TEMP TABLE _ev_snap_evicted AS
    SELECT c.subject_id, c.type_id, c.object_id, c.rating, c.rd, c.volatility,
           c.witness_count, c.last_observed_at
    FROM laplace.consensus c WHERE c.subject_id = subj;

    RAISE NOTICE '- evict: culled analysis-only cells, refolded survivors byte-identically, kept content, queued mask repair';
END $$;

-- Exercise both maintenance procedures far enough to compile and execute their
-- batched progress loggers. The perfcache is optional in a regress database, so
-- suppress its environment-dependent warning while retaining any ERROR.
SET client_min_messages = ERROR;
CALL laplace.highway_mask_drain(2);
CALL laplace.highway_mask_rebuild(100000);
RESET client_min_messages;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM laplace.highway_mask_dirty) THEN
        RAISE EXCEPTION 'FAIL: highway mask drain left queued entities';
    END IF;
    RAISE NOTICE '- highway mask drain and rebuild progress paths execute';
END $$;

-- Idempotence: evicting an already-evicted source changes nothing.
CALL ops.evict_source(laplace.source_id('EvictTestAnalysis'), NULL,
                  ARRAY[laplace.entity_type_id('Evict_TestMarker')],
                  p_batch => 2, p_drain => false);

-- Unseeded no-op: a source with zero rows evicts cleanly (0 rows, not an error).
CALL ops.evict_source(laplace.source_id('Evict_NoSuchSource'), NULL, NULL,
                  p_drain => false);

DO $$
DECLARE
    subj bytea := public.laplace_hash128_blake3('test/evict/subject');
    n    bigint;
BEGIN
    SELECT count(*) INTO n
    FROM laplace.consensus c
    JOIN _ev_snap_evicted s
      ON s.subject_id = c.subject_id AND s.type_id = c.type_id
     AND s.object_id IS NOT DISTINCT FROM c.object_id
     AND s.rating = c.rating AND s.rd = c.rd AND s.volatility = c.volatility
     AND s.witness_count = c.witness_count
     AND s.last_observed_at = c.last_observed_at
    WHERE c.subject_id = subj;
    IF n <> 4 OR (SELECT count(*) FROM laplace.consensus WHERE subject_id = subj) <> 4 THEN
        RAISE EXCEPTION 'FAIL: re-evict / unseeded evict mutated consensus';
    END IF;
    RAISE NOTICE '- re-evict and unseeded evict are no-ops';
END $$;

-- Re-derive at v2: the lane re-runs (markers are gone, so the hydrator re-yields
-- every unit) and deposits the SAME testimony under fresh evidence rows. Witness
-- counts return exactly to the v1 snapshot — the #508 acceptance: no double count.
DO $$
DECLARE
    src_a    bytea := laplace.source_id('EvictTestAnalysis');
    rel_p    bytea := public.laplace_hash128_blake3('test/evict/rel');
    rel_hot  bytea := laplace.relation_type_id('IS_A');
    subj     bytea := public.laplace_hash128_blake3('test/evict/subject');
    o1       bytea := public.laplace_hash128_blake3('test/evict/obj1');
    o2       bytea := public.laplace_hash128_blake3('test/evict/obj2');
    o4       bytea := public.laplace_hash128_blake3('test/evict/obj4');
    o5       bytea := public.laplace_hash128_blake3('test/evict/obj5');
    phi_a    bigint := 150000000000;
    win      bigint := 1000000000;
    s_conf   bigint := 900000000;
    s_ref    bigint := 100000000;
    t2       timestamptz := '2026-02-01 00:00:00+00';
    t3       timestamptz := '2026-03-01 00:00:00+00';
    affected bigint;
    n        bigint;
BEGIN
    INSERT INTO laplace.attestations
        (id, subject_id, type_id, object_id, source_id, context_id,
         outcome, last_observed_at, observation_count,
         sum_score_fp1e9, opponent_rd_fp1e9)
    VALUES
        (public.laplace_hash128_blake3('test/evict/v2/a1'), subj, rel_p, o1, src_a, NULL,
         2, t2, 2, 2 * s_conf, phi_a),
        (public.laplace_hash128_blake3('test/evict/v2/a2'), subj, rel_p, o1, src_a, NULL,
         1, t3, 1, s_ref, phi_a),
        (public.laplace_hash128_blake3('test/evict/v2/a3'), subj, rel_p, o2, src_a, NULL,
         2, t2, 2, s_conf, phi_a),
        (public.laplace_hash128_blake3('test/evict/v2/a4'), subj, rel_hot, o4, src_a, NULL,
         0, t2, 1, 0, phi_a),
        (public.laplace_hash128_blake3('test/evict/v2/a5'), subj, rel_p, NULL, src_a, NULL,
         2, t2, 3, 3 * win, phi_a),
        (public.laplace_hash128_blake3('test/evict/v2/a6'), subj, rel_p, o5, src_a, NULL,
         2, t2, 2, 2 * s_conf, phi_a);

    affected := consensus.upsert(
        ARRAY[subj, subj, subj, subj, subj],
        ARRAY[rel_p, rel_p, rel_hot, rel_p, rel_p],
        ARRAY[o1, o2, o4, NULL, o5],
        ARRAY[phi_a, phi_a, phi_a, phi_a, phi_a],
        ARRAY[2, 2, 1, 3, 2]::bigint[],
        ARRAY[2 * s_conf, s_conf, 0, 3 * win, 2 * s_conf]::bigint[],
        ARRAY[t2, t2, t2, t2, t2]);
    IF affected <> 5 THEN
        RAISE EXCEPTION 'FAIL: v2 analysis batch affected %, expected 5', affected;
    END IF;
    affected := consensus.upsert(
        ARRAY[subj], ARRAY[rel_p], ARRAY[o1],
        ARRAY[phi_a], ARRAY[1]::bigint[], ARRAY[s_ref]::bigint[], ARRAY[t3]);
    IF affected <> 1 THEN
        RAISE EXCEPTION 'FAIL: v2 t3 batch affected %, expected 1', affected;
    END IF;

    -- witness counts across EVERY cell equal the v1 snapshot: no double count
    SELECT count(*) INTO n
    FROM laplace.consensus c
    JOIN _ev_snap_v1 s
      ON s.subject_id = c.subject_id AND s.type_id = c.type_id
     AND s.object_id IS NOT DISTINCT FROM c.object_id
     AND s.witness_count = c.witness_count
    WHERE c.subject_id = subj;
    IF n <> 6 OR (SELECT count(*) FROM laplace.consensus WHERE subject_id = subj) <> 6 THEN
        RAISE EXCEPTION 'FAIL: v2 re-derive double-counted witnesses (% of 6 cells match v1)', n;
    END IF;

    -- cells whose analysis period was the LAST fold input reproduce v1 exactly
    -- (same period sequence). Cell VI's periods re-ordered around the eviction
    -- (t2 analysis now folds after t3), so only its witness count is pinned —
    -- sequential Glicko periods do not commute, and that order-dependence is the
    -- documented contract (annex 2.3), not drift.
    SELECT count(*) INTO n
    FROM laplace.consensus c
    JOIN _ev_snap_v1 s
      ON s.subject_id = c.subject_id AND s.type_id = c.type_id
     AND s.object_id IS NOT DISTINCT FROM c.object_id
     AND s.rating = c.rating AND s.rd = c.rd AND s.volatility = c.volatility
     AND s.witness_count = c.witness_count
     AND s.last_observed_at = c.last_observed_at
    WHERE c.subject_id = subj
      AND (c.object_id IS NULL OR c.object_id IN (o1, o2, o4));
    IF n <> 4 THEN
        RAISE EXCEPTION 'FAIL: only % of 4 tail-period cells reproduced v1 byte-identically', n;
    END IF;

    RAISE NOTICE '- re-derive at v2: witness counts equal the v1 snapshot (no double count)';
END $$;

-- Cleanup: this file COMMITs (the procedure requires it), so remove the fixture.
DO $$
DECLARE
    src_w bytea := laplace.source_id('EvictTestWitness');
    src_a bytea := laplace.source_id('EvictTestAnalysis');
    subj  bytea := public.laplace_hash128_blake3('test/evict/subject');
BEGIN
    DELETE FROM laplace.attestations WHERE source_id IN (src_w, src_a);
    DELETE FROM laplace.consensus WHERE subject_id = subj;
    DELETE FROM laplace.highway_mask_dirty WHERE id IN (
        subj,
        public.laplace_hash128_blake3('test/evict/obj1'),
        public.laplace_hash128_blake3('test/evict/obj2'),
        public.laplace_hash128_blake3('test/evict/obj4'),
        public.laplace_hash128_blake3('test/evict/obj5'));
    DELETE FROM laplace.entities WHERE id IN (
        src_w, src_a,
        public.laplace_hash128_blake3('test/evict/rel'), subj,
        public.laplace_hash128_blake3('test/evict/obj1'),
        public.laplace_hash128_blake3('test/evict/obj2'),
        public.laplace_hash128_blake3('test/evict/obj3'),
        public.laplace_hash128_blake3('test/evict/obj4'),
        public.laplace_hash128_blake3('test/evict/obj5'),
        public.laplace_hash128_blake3('test/evict/marker-of-witness'),
        public.laplace_hash128_blake3('test/evict/content-by-analysis'));
    DROP TABLE IF EXISTS _ev_snap_v1;
    DROP TABLE IF EXISTS _ev_snap_evicted;
    RAISE NOTICE '- evict fixture cleaned up';
END $$;
